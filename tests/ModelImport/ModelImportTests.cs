using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using WowDi.Contracts;
using WowDi.ModelImport;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class ModelImportTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"));
    private static byte[] Edit(Action<JsonObject> edit)
    {
        var node=JsonNode.Parse(Fixture())!.AsObject();
        edit(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }
    [Fact]
    public void EmbeddedSchemaIsStandardAndValidatesRealFactoryFixture()
    {
        var raw=VersionedModelImport.GetSchemaV1();
        using var doc=JsonDocument.Parse(raw);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema",doc.RootElement.GetProperty("$schema").GetString());
        var schema=JsonSchema.FromText(raw, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        using var fixture=JsonDocument.Parse(Fixture());
        Assert.True(schema.Evaluate(fixture.RootElement).IsValid);
        Assert.Null(VersionedModelImport.ValidateSchema(Fixture()));
        var imported=VersionedModelImport.Read(Fixture());
        Assert.True(imported.Accepted);
        var model=Assert.IsType<UdirDocument>(imported.Document);
        Assert.Equal(UdirDocument.Read(Fixture()).Write(),model.Write());
        Assert.Equal(model.Write(),VersionedModelImport.Read(model.Write()).Document!.Write());
    }
    [Theory]
    [InlineData("wrongType")]
    [InlineData("missing")]
    [InlineData("unknownField")]
    [InlineData("unknownEnum")]
    [InlineData("unknownSemantics")]
    [InlineData("malformedBound")]
    [InlineData("oversizedBound")]
    [InlineData("badHash")]
    [InlineData("nestedUnknownField")]
    public void RejectsInvalidWireShape(string mutation)
    {
        var bytes=Edit(root=>{
            var p=root["payload"]!.AsObject();var symbol=p["symbols"]![0]!.AsObject();
            switch(mutation)
            {
                case "wrongType": symbol["upper"]=12;break;
                case "missing": p.Remove("scenarios");break;
                case "unknownField": p["invented"]=true;break;
                case "unknownEnum": symbol["role"]="Unknown";break;
                case "unknownSemantics": p["semanticsVersion"]="udir/2";break;
                case "malformedBound":symbol["upper"]="1/0";break;
                case "oversizedBound":symbol["upper"]=new string('9',257);break;
                case "badHash":p["inputDigests"]![0]="BAD";break;
                case "nestedUnknownField":symbol["span"]!["invented"]=0;break;
            }
        });
        var rejected=VersionedModelImport.Read(bytes);
        Assert.False(rejected.Accepted);
        Assert.Null(rejected.Document);
        Assert.Equal(new ModelImportDiagnostic(ModelImportStage.Schema,"modelImport.schema"),rejected.Diagnostic);
    }
    [Theory]
    [InlineData("reversedBounds")]
    [InlineData("nonReduced")]
    [InlineData("danglingReference")]
    [InlineData("outOfBoundsSpan")]
    [InlineData("duplicateSymbol")]
    [InlineData("fractionalInteger")]
    public void SchemaPassDoesNotImplyDomainValidity(string mutation)
    {
        var bytes=Edit(root=>{
            var p=root["payload"]!.AsObject();var symbols=p["symbols"]!.AsArray();var symbol=symbols[0]!.AsObject();
            switch(mutation)
            {
                case "reversedBounds":symbol["lower"]="20";break;
                case "nonReduced":symbols[2]!["lower"]="2/4";break;
                case "danglingReference":symbol["typeId"]="absent";break;
                case "outOfBoundsSpan":symbol["span"]!["start"]=10000;break;
                case "duplicateSymbol":symbols.Add(symbol.DeepClone());break;
                case "fractionalInteger":symbol["upper"]="3/2";break;
            }
        });
        Assert.Null(VersionedModelImport.ValidateSchema(bytes));
        var rejected=VersionedModelImport.Read(bytes);
        Assert.False(rejected.Accepted);
        Assert.Null(rejected.Document);
        Assert.Equal(ModelImportStage.Domain,rejected.Diagnostic!.Stage);
        Assert.StartsWith("udir.",rejected.Diagnostic.Code);
    }
    [Theory]
    [InlineData("2.0","contract.major")]
    [InlineData("1.01","contract.version")]
    [InlineData("1.65536","contract.version")]
    public void RejectsUnsupportedEnvelopeVersions(string version,string code)
    {
        var result=VersionedModelImport.Read(Edit(r=>r["version"]=version));
        Assert.Equal(new ModelImportDiagnostic(ModelImportStage.Compatibility,code),result.Diagnostic);
    }
    [Fact]
    public void CompatibleMinorAndOpaqueOptionalMetadataAreRetained()
    {
        var bytes=Edit(r=>{
            r["version"]="1.65535";
            r["extensions"]!["future.optional"]=new JsonObject {["required"]=false,["value"]=new JsonObject{["flag"]=true}};
        });
        var imported=VersionedModelImport.Read(bytes);
        Assert.True(imported.Accepted);
        Assert.Equal(ushort.MaxValue,imported.Document!.MinorVersion);
        Assert.True(imported.Document.Extensions!.Value.GetProperty("future.optional").GetProperty("value").GetProperty("flag").GetBoolean());
        Assert.Equal(imported.Document.Write(),VersionedModelImport.Read(imported.Document.Write()).Document!.Write());
    }
    [Fact]
    public void UnknownRequiredExtensionAndDuplicateNamesFailBeforeDomain()
    {
        var mandatory=Edit(r=>r["extensions"]!["future.required"]=new JsonObject {["required"]=true,["value"]=1});
        Assert.Equal(new ModelImportDiagnostic(ModelImportStage.Compatibility,"extension.required"),VersionedModelImport.Read(mandatory).Diagnostic);
        var original=Encoding.UTF8.GetString(Fixture());
        var duplicated=Encoding.UTF8.GetBytes(original.Replace("\"tenantId\": \"fixture\",","\"tenantId\": \"fixture\", \"tenantId\": \"other\",",StringComparison.Ordinal));
        Assert.Equal(new ModelImportDiagnostic(ModelImportStage.Compatibility,"envelope.json"),VersionedModelImport.Read(duplicated).Diagnostic);
    }
    [Fact]
    public void ImportDoesNotTreatUnknownRequiredCapabilityAsSupported()
    {
        var bytes=Edit(r=>r["payload"]!["requires"]!.AsArray().Add("future.engine.capability"));
        var result=VersionedModelImport.Read(bytes);
        Assert.True(result.Accepted);
        Assert.Contains("future.engine.capability",result.Document!.Model.Requires);
    }
    [Fact]
    public void SizeAndDepthLimitsAreEnforcedBeforeSchema()
    {
        Assert.Equal(new ModelImportDiagnostic(ModelImportStage.Compatibility,"envelope.size"),
            VersionedModelImport.Read(new byte[EnvelopeReader.MaximumEnvelopeBytes+1]).Diagnostic);
        var text="{\"contract\":\"wowdi.udir\",\"version\":\"1.0\",\"payload\":{\"deep\":"+new string('[',70)+"0"+new string(']',70)+"}}";
        Assert.Equal(new ModelImportDiagnostic(ModelImportStage.Compatibility,"envelope.json"),
            VersionedModelImport.Read(Encoding.UTF8.GetBytes(text)).Diagnostic);
    }
    [Fact]
    public async Task ConcurrentReadersReturnSameFrozenModel()
    {
        var results=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>VersionedModelImport.Read(Fixture()))));
        var expected=UdirDocument.Read(Fixture()).Write();
        Assert.All(results,r=>{Assert.True(r.Accepted);Assert.Equal(expected,r.Document!.Write());});
    }
}
