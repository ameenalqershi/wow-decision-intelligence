using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WowDi.Contracts;
using Xunit;

namespace WowDi.Integration.Connectors.Tests;

public sealed class ConnectorContractTests
{
    private const string Fixture = """
    {
      "contract": "wowdi.integration.connector-manifest",
      "version": "1.0",
      "payload": {
        "connectorKind": "synthetic.csv",
        "sourceSchema": "factory-fixture",
        "sourceSchemaVersion": "2026.1",
        "targetSchema": "factory-operations",
        "targetSchemaVersion": "1.0",
        "mappingVersion": "1.0",
        "fields": [
          {"sourceField":"Operation","targetField":"F-OP","sourceType":"text","targetType":"text","sourceUnit":null,"targetUnit":null,"required":true,"allowNull":false,"transformId":"identity","transformVersion":"1.0"},
          {"sourceField":"Duration","targetField":"F-DURATION","sourceType":"duration","targetType":"duration","sourceUnit":"minute","targetUnit":"minute","required":true,"allowNull":false,"transformId":"identity","transformVersion":"1.0"}
        ],
        "provenance":{"sourceKeyFields":["Operation"],"sourceTimestampField":"Modified"},
        "cursor":{"mode":"offset","maxBatchRows":100},
        "retry":{"maxAttempts":3,"initialDelayMs":100,"maxDelayMs":2000},
        "reconciliation":"none",
        "timePolicy":"explicit-offset",
        "timeZoneId":null,
        "currencyPolicy":"none",
        "credentialReference":"cred:synthetic-read"
      }
    }
    """;
    private static DecodedConnectorManifest Read(string json) => ConnectorManifestReader.Read(Encoding.UTF8.GetBytes(json));
    private static ConnectorManifest Manifest() => Read(Fixture).Manifest;
    private static JsonObject Root() => JsonNode.Parse(Fixture)!.AsObject();

    [Fact]
    public void ReadsTypedManifestAndPreservesUnknownOptionalExtension()
    {
        var root = Root();
        root["version"] = "1.1";
        root["extensions"] = JsonNode.Parse("""{"ui.label":{"required":false,"value":"Factory"}}""");
        var result = Read(root.ToJsonString());
        Assert.Equal(1, result.WireMinor);
        Assert.Equal("minute", result.Manifest.Fields[1].TargetUnit);
        Assert.Equal("cred:synthetic-read", result.Manifest.CredentialReference);
        Assert.Equal("Factory", result.Extensions!.Value.GetProperty("ui.label").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("contract", "wrong", "contract.identity")]
    [InlineData("version", "2.0", "contract.major")]
    [InlineData("version", "01.0", "contract.version")]
    public void RejectsIncompatibleEnvelope(string key, string value, string code)
    {
        var root = Root();
        root[key] = value;
        Assert.Equal(code, Assert.Throws<ContractException>(() => Read(root.ToJsonString())).Code);
    }

    [Fact]
    public void RejectsDuplicateJsonAndUnknownRequiredExtension()
    {
        Assert.Equal("envelope.json", Assert.Throws<ContractException>(() =>
            Read(Fixture.Replace("\"mappingVersion\": \"1.0\"", "\"mappingVersion\": \"1.0\", \"mappingVersion\": \"2.0\""))).Code);
        var root = Root();
        root["extensions"] = JsonNode.Parse("""{"security.scope":{"required":true,"value":"unknown"}}""");
        Assert.Equal("extension.required", Assert.Throws<ContractException>(() => Read(root.ToJsonString())).Code);
    }

    [Theory]
    [InlineData("credentialReference","https://example.test/secret","connector.credentials")]
    [InlineData("credentialReference","raw-token","connector.credentials")]
    [InlineData("credentialReference","cred:../../secret","connector.credentials")]
    [InlineData("mappingVersion","01.0","connector.identity")]
    [InlineData("timePolicy","local-default","connector.time")]
    [InlineData("currencyPolicy","implicit","connector.currency")]
    [InlineData("reconciliation","blind-retry","connector.reconciliation")]
    public void RejectsUnsafeManifestValues(string field, string value, string code)
    {
        var root = Root();
        root["payload"]![field] = value;
        var exception = Assert.Throws<ContractException>(() => Read(root.ToJsonString()));
        Assert.Equal(code, exception.Code);
        Assert.DoesNotContain(value, exception.Message);
    }

    [Fact]
    public void RequiresExplicitFieldsAndRejectsSecretOrExecutablePayload()
    {
        foreach (var key in new[] {"password","apiKey","script","tenantId"})
        {
            var root = Root();
            root["payload"]![key] = "DO-NOT-LEAK";
            var ex = Assert.Throws<ContractException>(() => Read(root.ToJsonString()));
            Assert.Equal("connector.shape", ex.Code);
            Assert.DoesNotContain("DO-NOT-LEAK", ex.Message);
        }
        var missing = Root();
        missing["payload"]!.AsObject().Remove("credentialReference");
        Assert.Equal("connector.shape", Assert.Throws<ContractException>(() => Read(missing.ToJsonString())).Code);
        var nullRef = Root();
        nullRef["payload"]!["credentialReference"] = null;
        Assert.Null(Read(nullRef.ToJsonString()).Manifest.CredentialReference);
    }

    [Fact]
    public void RejectsDuplicateTargetsWrongUnitsAndCurrencyConversionWithoutSnapshot()
    {
        var m = Manifest();
        var field = m.Fields[1];
        AssertCode("connector.fields", m with { Fields = [m.Fields[0], field with { TargetField = m.Fields[0].TargetField }] });
        AssertCode("connector.unit", m with { Fields = [field with { SourceUnit = null }] });
        AssertCode("connector.transform", m with { Fields = [field with { TargetUnit = "hour" }] });
        AssertCode("connector.transform", m with { Fields = [field with { TransformId = "eval(x)" }] });
        var money = field with { SourceType = "money", TargetType = "money", SourceUnit = "USD", TargetUnit = "EUR", TransformId = "fx" };
        AssertCode("connector.currency", m with { Fields = [money] });
        AssertCode("connector.currency", m with { Fields = [money], CurrencyPolicy = "explicit-currency" });
        ConnectorManifestReader.Validate(m with { Fields = [money], CurrencyPolicy = "rate-snapshot" });
    }

    [Fact]
    public void EnforcesProvenanceRetryTimeAndCursorLimits()
    {
        var m = Manifest();
        AssertCode("connector.provenance", m with { Provenance = m.Provenance with { SourceKeyFields = ["id","id"] } });
        AssertCode("connector.provenance", m with { Provenance = m.Provenance with { SourceTimestampField = "" } });
        AssertCode("connector.cursor", m with { Cursor = m.Cursor with { MaxBatchRows = 10001 } });
        AssertCode("connector.retry", m with { Retry = m.Retry with { MaxAttempts = 11 } });
        AssertCode("connector.retry", m with { Retry = m.Retry with { MaxDelayMs = 99 } });
        AssertCode("connector.time", m with { TimePolicy = "named-zone", TimeZoneId = "not-a-real-zone" });
        ConnectorManifestReader.Validate(m with { TimePolicy = "named-zone", TimeZoneId = "UTC" });
    }

    private static void AssertCode(string code, ConnectorManifest manifest) =>
        Assert.Equal(code, Assert.Throws<ContractException>(() => ConnectorManifestReader.Validate(manifest)).Code);

    private static ConnectorReadRequest Request(ConnectorPosition? after = null) =>
        new(new("tenant-a","instance-a","1.0","stream-a"), after, 2);

    private static ConnectorSourceRow Row() => new(["op-1"], DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        JsonSerializer.SerializeToElement(new { Operation = "op-1", Duration = 15 }));

    [Fact]
    public void OffsetCheckpointsRequireProgressAndNeverRegress()
    {
        var m = Manifest();
        var request = Request(new(null, 10));
        var valid = new ConnectorReadBatch([Row()], new(null,11), true);
        ConnectorReadContract.ValidateBatch(m,request,valid);
        foreach (var next in new ConnectorPosition?[] { null,new(null,9),new(null,10),new("opaque",null),new(null,-1) })
            Assert.Equal("connector.position", Assert.Throws<ContractException>(() =>
                ConnectorReadContract.ValidateBatch(m,request,valid with { Next = next })).Code);
        ConnectorReadContract.ValidateBatch(m,request,new([],null,true));
        Assert.Equal("connector.position", Assert.Throws<ContractException>(() =>
            ConnectorReadContract.ValidateBatch(m,request,new([],new(null,9),true))).Code);
    }

    [Fact]
    public void SnapshotContinuationIsNotACompletedCheckpoint()
    {
        var m = Manifest();
        m = m with { Cursor = m.Cursor with { Mode = "snapshot" } };
        var first = new ConnectorReadBatch([Row()],new("page-2",null),false);
        ConnectorReadContract.ValidateBatch(m,Request(),first);
        var request = Request(first.Next);
        ConnectorReadContract.ValidateBatch(m,request,new([Row()],null,true));
        Assert.Equal("connector.position", Assert.Throws<ContractException>(() =>
            ConnectorReadContract.ValidateBatch(m,request,first)).Code);
        Assert.Equal("connector.position", Assert.Throws<ContractException>(() =>
            ConnectorReadContract.ValidateBatch(m,request,first with { Complete = true })).Code);
    }

    [Fact]
    public void RejectsWrongStreamMappingOversizedBatchAndMalformedRows()
    {
        var m = Manifest();
        var request = Request();
        Assert.Equal("connector.stream", Assert.Throws<ContractException>(() =>
            ConnectorReadContract.ValidateRequest(m,request with { Stream = request.Stream with { MappingVersion = "2.0" } })).Code);
        Assert.Equal("connector.batch", Assert.Throws<ContractException>(() =>
            ConnectorReadContract.ValidateRequest(m,request with { MaxRows = 101 })).Code);
        Assert.Equal("connector.batch", Assert.Throws<ContractException>(() =>
            ConnectorReadContract.ValidateBatch(m,request,new([Row(),Row(),Row()],new(null,3),true))).Code);
        foreach (var row in new[] { Row() with { SourceKey = [] }, Row() with { SourceTimestamp = default },
            Row() with { Values = JsonSerializer.SerializeToElement("not-object") } })
            Assert.Equal("connector.row", Assert.Throws<ContractException>(() =>
                ConnectorReadContract.ValidateBatch(m,request,new([row],new(null,1),true))).Code);
    }
}
