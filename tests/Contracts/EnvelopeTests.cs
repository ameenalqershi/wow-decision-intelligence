using System.Text;
using System.Text.Json;
using WowDi.Contracts;
using Xunit;

namespace WowDi.Contracts.Tests;

public sealed class EnvelopeTests
{
    private const string Id = "wowdi.example.horizon";
    private const string Original = """{"contract":"wowdi.example.horizon","version":"1.0","payload":{"minutes":3}}""";

    private static ContractEnvelope Read(string text, IReadOnlyDictionary<string, Action<JsonElement>>? handlers = null)
        => EnvelopeReader.Read(Encoding.UTF8.GetBytes(text), Id, 1, handlers);

    private static string WithExtension(bool required, string value = "7")
        => Original[..^1] + ""","extensions":{"org.example.display":{"required":""" +
            (required ? "true" : "false") + ""","value":""" + value + "}}}";

    [Fact]
    public void NewerMinorAndUnknownOptionalMetadataRetainOriginalMeaning()
    {
        var result = Read(WithExtension(false).Replace("1.0", "1.1"));
        Assert.Equal(3, result.Payload.GetProperty("minutes").GetInt32());
        Assert.Equal(7, result.Extensions!.Value.GetProperty("org.example.display").GetProperty("value").GetInt32());
        Assert.Equal(1, result.Minor);
    }

    [Fact]
    public void ReaderWithNewHandlerAcceptsOldEnvelope()
    {
        var registry = new Dictionary<string, Action<JsonElement>> { ["org.example.display"] = _ => Assert.Fail("Absent extension") };
        Assert.Equal(0, Read(Original, registry).Minor);
    }

    [Fact]
    public void RequiredExtensionNeedsAnExactHandlerAndAValidValue()
    {
        var seen = 0;
        var registry = new Dictionary<string, Action<JsonElement>>(StringComparer.OrdinalIgnoreCase)
        {
            ["org.example.display"] = value => seen = value.ValueKind == JsonValueKind.Number
                ? value.GetInt32() : throw new ContractException("display.value", "Expected integer.")
        };
        Read(WithExtension(true), registry);
        Assert.Equal(7, seen);
        Assert.Equal("extension.required", Assert.Throws<ContractException>(() => Read(WithExtension(true))).Code);
        Assert.Equal("extension.required", Assert.Throws<ContractException>(() =>
            Read(WithExtension(true).Replace("org.example.display", "ORG.EXAMPLE.DISPLAY"), registry)).Code);
        Assert.Equal("display.value", Assert.Throws<ContractException>(() => Read(WithExtension(true, "null"), registry)).Code);
    }

    [Theory]
    [InlineData("0.0")] [InlineData("01.0")] [InlineData("1.01")] [InlineData("1")]
    [InlineData("1.0.0")] [InlineData("1.+1")] [InlineData("1.65536")] [InlineData("１.０")]
    public void RejectsNonCanonicalVersions(string version)
        => Assert.Equal("contract.version", Assert.Throws<ContractException>(() => Read(Original.Replace("1.0", version))).Code);

    [Theory]
    [InlineData("[]", "envelope.shape")]
    [InlineData("{", "envelope.json")]
    [InlineData("""{"contract":"a","contract":"b"}""", "envelope.json")]
    [InlineData("""{"extra":true}""", "envelope.field")]
    public void ReportsStableStructuralDiagnostics(string json, string expected)
        => Assert.Equal(expected, Assert.Throws<ContractException>(() => Read(json)).Code);

    [Fact]
    public void RejectsWrongContractMajorAndMalformedExtensions()
    {
        Assert.Equal("contract.identity", Assert.Throws<ContractException>(() => Read(Original.Replace(Id, "other"))).Code);
        Assert.Equal("contract.major", Assert.Throws<ContractException>(() => Read(Original.Replace("1.0", "2.0"))).Code);
        Assert.Equal("extension.shape", Assert.Throws<ContractException>(() =>
            Read(WithExtension(false).Replace("\"required\":false,", ""))).Code);
    }

    [Fact]
    public void BoundedInputAndNestedDuplicatesAreRejected()
    {
        Assert.Equal("envelope.size", Assert.Throws<ContractException>(() =>
            EnvelopeReader.Read(new byte[EnvelopeReader.MaximumEnvelopeBytes + 1], Id, 1)).Code);
        Assert.Equal("envelope.json", Assert.Throws<ContractException>(() =>
            Read(Original.Replace("\"minutes\":3", "\"minutes\":1,\"minutes\":3"))).Code);
    }

    [Fact]
    public void ExplicitExampleMigrationPreservesExactDuration()
    {
        var v1 = Read(Original);
        int minutes = v1.Payload.GetProperty("minutes").GetInt32();
        var v2Bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = Id, version = "2.0", payload = new { duration = new { value = minutes, unit = "minute" } }
        });
        var v2 = EnvelopeReader.Read(v2Bytes, Id, 2);
        var duration = v2.Payload.GetProperty("duration");
        Assert.Equal(minutes, duration.GetProperty("value").GetInt32());
        Assert.Equal("minute", duration.GetProperty("unit").GetString());
        Assert.Equal(3, v1.Payload.GetProperty("minutes").GetInt32());
    }
}
