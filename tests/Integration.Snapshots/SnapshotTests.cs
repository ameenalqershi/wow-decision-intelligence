using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using WowDi.Enterprise.Identity;
using WowDi.Integration.Connectors;
using Xunit;

namespace WowDi.Integration.Snapshots.Tests;

public sealed class SnapshotTests
{
    private static readonly ConnectorStreamIdentity Stream = new("fixture","connector","1.0","orders");
    private static readonly TargetSchema Target = new("factory-operations","1",
        [new("F-OP","text",null,true,false),new("F-MACHINE","text",null,true,false),
         new("F-DURATION","quantity","minute",true,false,Integral:true,Positive:true),new("F-WEIGHT","decimal",null,true,false,NonNegative:true),
         new("note","text",null,false,true)]);
    private static FieldMapping Field(string source,TargetField field) => new()
    { SourceField=source,TargetField=field.Id,SourceType=field.Type,TargetType=field.Type,
      SourceUnit=field.Unit,TargetUnit=field.Unit,Required=field.Required,AllowNull=field.AllowNull,
      TransformId="identity",TransformVersion="1.0" };
    private static ConnectorManifest Manifest()=>new()
    {
        ConnectorKind="file",SourceSchema="orders",SourceSchemaVersion="1",
        TargetSchema=Target.Id,TargetSchemaVersion=Target.Version,MappingVersion="1.0",
        Fields=[Field("id",Target.Fields[0]),Field("machine",Target.Fields[1]),Field("minutes",Target.Fields[2]),
            Field("weight",Target.Fields[3]),Field("note",Target.Fields[4])],
        Provenance=new(){SourceKeyFields=["id"],SourceTimestampField="updated"},
        Cursor=new(){Mode="snapshot",MaxBatchRows=100},Retry=new(){MaxAttempts=1,InitialDelayMs=1,MaxDelayMs=1},
        Reconciliation="none",TimePolicy="utc-only",TimeZoneId=null,CurrencyPolicy="none",CredentialReference="cred:fixture"
    };
    private static byte[] Encode(ConnectorManifest m)=>JsonSerializer.SerializeToUtf8Bytes(new
    {contract=ConnectorManifestReader.ContractId,version="1.0",payload=m},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
    private static byte[] Bytes(string value)=>Encoding.UTF8.GetBytes(value);
    private const string Header="id;machine;minutes;weight;note;updated\r\n";
    private const string Stamp="2026-01-01T00:00:00Z";
    private static string Row(string id,string minutes="3",string weight="2,50",string note="\\N")=>$"{id};M1;{minutes};{weight};{note};{Stamp}\r\n";
    private static SnapshotResult Csv(string csv,ConnectorManifest? m=null,TargetSchema? target=null)=>
        SnapshotImport.Stage(Stream,Encode(m??Manifest()),target??Target,Bytes(csv),new(SnapshotFormat.Csv,';',','));
    private static SnapshotResult Json(string json,ConnectorManifest? m=null)=>
        SnapshotImport.Stage(Stream,Encode(m??Manifest()),Target,Bytes(json),new(SnapshotFormat.Json));

    [Fact]
    public void CsvRepeatAndReverseOrderKeepSemanticDigestButNotSourceDigest()
    {
        var first=Assert.IsType<StagedSnapshot>(Csv(Header+Row("A")+Row("B","4","0")).Snapshot);
        var retry=Assert.IsType<StagedSnapshot>(Csv(Header+Row("A")+Row("B","4","0")).Snapshot);
        var reverse=Assert.IsType<StagedSnapshot>(Csv(Header+Row("B","4","0")+Row("A")).Snapshot);
        Assert.Equal(first.SemanticSha256,retry.SemanticSha256);
        Assert.Equal(first.SemanticSha256,reverse.SemanticSha256);
        Assert.Equal(first.OriginalSha256,retry.OriginalSha256);
        Assert.NotEqual(first.OriginalSha256,reverse.OriginalSha256);
        Assert.Equal("A",first.Rows[0].SourceKey[0]);
        Assert.Equal("2.5",first.Rows[0].Values["F-WEIGHT"].Value);
        Assert.Equal("minute",first.Rows[0].Values["F-DURATION"].Unit);
        Assert.Equal("0",first.Rows[1].Values["F-WEIGHT"].Value);
        Assert.Null(first.Rows[0].Values["note"].Value);
        Assert.Equal(Stamp,first.Rows[0].OriginalTimestamp);
        Assert.Equal(TimeSpan.Zero,first.Rows[0].SourceTimestamp.Offset);
    }

    [Fact]
    public void JsonPropertyAndRowOrderIndependentAndOptionalNullDistinctFromAbsence()
    {
        const string a="""{"id":"A","machine":"M1","minutes":3,"weight":2.500,"updated":"2026-01-01T00:00:00Z","note":null}""";
        const string b="""{"updated":"2026-01-01T00:00:00Z","weight":0,"minutes":4,"machine":"M1","id":"B"}""";
        var one=Assert.IsType<StagedSnapshot>(Json("["+a+","+b+"]").Snapshot);
        var two=Assert.IsType<StagedSnapshot>(Json("["+b+","+a+"]").Snapshot);
        Assert.Equal(one.SemanticSha256,two.SemanticSha256);
        Assert.Equal("2.5",one.Rows[0].Values["F-WEIGHT"].Value);
        Assert.Null(one.Rows[0].Values["note"].Value);
        Assert.False(one.Rows[1].Values.ContainsKey("note"));
        Assert.NotEqual(one.SemanticSha256,Json("["+a.Replace("null","\"\"")+","+b+"]").Snapshot!.SemanticSha256);
    }

    [Fact]
    public void EncodingAndCsvQuotedFieldsPreserveUtf8AndMultiline()
    {
        var data=Bytes(Header+Row("عميل",note:"\"a;\"\"b\"\"\nس\""));
        var bom=new byte[]{0xef,0xbb,0xbf}.Concat(data).ToArray();
        var result=SnapshotImport.Stage(Stream,Encode(Manifest()),Target,bom,new(SnapshotFormat.Csv,';',','));
        Assert.True(result.Accepted);
        Assert.Equal("عميل",result.Snapshot!.Rows[0].SourceKey[0]);
        Assert.Equal("a;\"b\"\nس",result.Snapshot.Rows[0].Values["note"].Value);
        var invalid=SnapshotImport.Stage(Stream,Encode(Manifest()),Target,new byte[]{0xff,0xfe,0x41,0x00},new(SnapshotFormat.Csv,';',','));
        Assert.Equal("snapshot.encoding",Assert.Single(invalid.Diagnostics).Code);
        Assert.Null(invalid.Snapshot);
        Assert.Equal("\\N",Csv(Header+Row("A",note:"\"\\N\"")).Snapshot!.Rows[0].Values["note"].Value);
        Assert.Equal("",Csv(Header+Row("A",note:"")).Snapshot!.Rows[0].Values["note"].Value);
    }

    [Theory]
    [InlineData("1.234,5")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    [InlineData("+1")]
    [InlineData("1,")]
    [InlineData(" 1")]
    public void DecimalAmbiguityRejectedWithoutPartialRows(string weight)
    {
        var result=Csv(Header+Row("A")+Row("B",weight:weight));
        Assert.Null(result.Snapshot);
        Assert.Contains(result.Diagnostics,d=>d.Row==2 && d.TargetField=="F-WEIGHT" && d.Code=="snapshot.value");
    }

    [Fact]
    public void NumericNormalizationIsExactAndCultureIndependent()
    {
        var before=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
            var raw="123456789012345678901234567890,1234567890123456789000";
            var result=Csv(Header+Row("A",weight:raw));
            Assert.Equal("123456789012345678901234567890.1234567890123456789",result.Snapshot!.Rows[0].Values["F-WEIGHT"].Value);
            Assert.Equal("0",Csv(Header+Row("A",weight:"-000,000")).Snapshot!.Rows[0].Values["F-WEIGHT"].Value);
        }
        finally { CultureInfo.CurrentCulture=before; }
    }

    [Fact]
    public void DiagnosticsIdentifyRowsAndTargetFieldsWithoutLeakingRawValues()
    {
        var input=Header+Row("A",minutes:"SECRET") + Row("B",weight:"\\N");
        var result=Csv(input);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.Diagnostics,d=>d.Row==1&&d.TargetField=="F-DURATION"&&d.Code=="snapshot.value");
        Assert.Contains(result.Diagnostics,d=>d.Row==2&&d.TargetField=="F-WEIGHT"&&d.Code=="snapshot.null");
        Assert.DoesNotContain("SECRET",JsonSerializer.Serialize(result.Diagnostics));
        Assert.Equal("snapshot.duplicateKey",Assert.Single(Csv(Header+Row("A")+Row("A")).Diagnostics).Code);
        var malformed=Csv(Header+"bad;row\n"+Row("B",minutes:"0")+Row("C",minutes:"1,5")+Row("D",weight:"-1"));
        Assert.Equal(new[]{1,2,3,4},malformed.Diagnostics.Select(d=>d.Row).ToArray());
        Assert.Null(malformed.Snapshot);
        Assert.Contains(Json("""[{"id":"A","machine":"M1","weight":2,"updated":"2026-01-01T00:00:00Z"}]""").Diagnostics,
            d=>d.TargetField=="F-DURATION"&&d.Code=="snapshot.required");
    }

    [Theory]
    [InlineData("id;id\nA;B\n","snapshot.header")]
    [InlineData("id;machine\nA\n","snapshot.columnCount")]
    [InlineData("id\n\"unclosed","snapshot.csvSyntax")]
    [InlineData("id\nA\"","snapshot.csvSyntax")]
    [InlineData("id\rA","snapshot.csvSyntax")]
    public void MalformedCsvRejected(string input,string code)=>Assert.Contains(Csv(input).Diagnostics,d=>d.Code==code);

    [Theory]
    [InlineData("[{\"id\":\"A\",\"id\":\"B\"}]","snapshot.json")]
    [InlineData("[{\"id\":{}}]","snapshot.cellShape")]
    [InlineData("{}","snapshot.shape")]
    [InlineData("[]","snapshot.empty")]
    public void MalformedJsonRejected(string input,string code)=>Assert.Contains(Json(input).Diagnostics,d=>d.Code==code);

    [Fact]
    public void ManifestTargetAndTransformMustBeApprovedAndVersionBound()
    {
        var m=Manifest();
        Assert.Equal("snapshot.targetSchema",Assert.Single(Csv(Header+Row("A"),target:Target with {Version="2"}).Diagnostics).Code);
        var altered=m with {Fields=m.Fields.SetItem(2,m.Fields[2] with {TransformId="unregistered"})};
        Assert.Equal("snapshot.unsupportedTransform",Assert.Single(Csv(Header+Row("A"),altered).Diagnostics).Code);
        Assert.Equal("snapshot.targetSchema",Assert.Single(Csv(Header+Row("A"),target:Target with {Fields=Target.Fields.SetItem(2,Target.Fields[2] with {Unit="hour"})}).Diagnostics).Code);
        Assert.Equal("connector.stream",Assert.Single(Csv(Header+Row("A"),m with {MappingVersion="2.0"}).Diagnostics).Code);
        Assert.Equal("snapshot.mode",Assert.Single(Csv(Header+Row("A"),m with {Cursor=m.Cursor with {Mode="offset"}}).Diagnostics).Code);
    }

    [Fact]
    public void ExplicitTimeAndNamedZoneRejectAmbiguousAndNonexistentTimes()
    {
        var m=Manifest();
        string input=Header+Row("A").Replace(Stamp,"2026-01-01T03:00:00+03:00");
        Assert.False(Csv(input,m).Accepted);
        var accepted=Csv(input,m with {TimePolicy="explicit-offset"});
        Assert.Equal(DateTimeOffset.Parse(Stamp,CultureInfo.InvariantCulture),accepted.Snapshot!.Rows[0].SourceTimestamp);
        var named=m with {TimePolicy="named-zone",TimeZoneId="America/New_York"};
        Assert.False(Csv(Header+Row("A").Replace(Stamp,"2026-11-01T01:30:00"),named).Accepted);
        Assert.False(Csv(Header+Row("A").Replace(Stamp,"2026-03-08T02:30:00"),named).Accepted);
        Assert.True(Csv(Header+Row("A").Replace(Stamp,"2026-01-01T12:30:00"),named).Accepted);
    }

    [Fact]
    public void LimitsAndCancellationFailClosed()
    {
        var m=Manifest();
        Assert.Equal("snapshot.rows",Assert.Single(Csv(Header+Row("A")+Row("B"),m with {Cursor=m.Cursor with {MaxBatchRows=1}}).Diagnostics).Code);
        Assert.Equal("snapshot.size",Assert.Single(SnapshotImport.Stage(Stream,Encode(m),Target,new byte[4*1024*1024+1],new(SnapshotFormat.Json)).Diagnostics).Code);
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>SnapshotImport.Stage(Stream,Encode(m),Target,Bytes(Header+Row("A")),new(SnapshotFormat.Csv,';',','),cancellation.Token));
        var mappings=Enumerable.Range(0,1000).Select(i=>new TargetField("field"+i,"text",null,true,false)).ToImmutableArray();
        var target=new TargetSchema("large","1",mappings);
        var many=m with {TargetSchema="large",Fields=mappings.Select(f=>Field("note",f)).ToImmutableArray()};
        Assert.Equal("snapshot.normalizedSize",Assert.Single(Csv(Header+Row("A",note:new string('a',8000)),many,target).Diagnostics).Code);
    }

    [Fact]
    public void GeneratedUdirUsesM10AndTrustedTenant()
    {
        var bytes=System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"));
        Assert.True(SnapshotImport.ValidateGeneratedModel(Stream,bytes).Accepted);
        Assert.Equal("snapshot.tenant",SnapshotImport.ValidateGeneratedModel(Stream with {TenantId="other"},bytes).Diagnostic!.Code);
        Assert.False(SnapshotImport.ValidateGeneratedModel(Stream,Bytes("{}")).Accepted);
    }

    [Fact]
    public async Task PreviewAuthorizesBeforeRegistryLookupAndRejectsMismatchedRegistryScope()
    {
        var registry=new Registry(new(Stream,Encode(Manifest()),Target));
        using var services=new ServiceCollection().AddSingleton<IAuthenticationService>(new Authentication()).BuildServiceProvider();
        var context=new DefaultHttpContext{RequestServices=services};
        var access=new TenantAccess(new Accessor{HttpContext=context},new Directory(),new Audit(),new("https://issuer.example.test","api"));
        var preview=new MappingPreview(access,registry);
        var input=Bytes(Header+Row("A"));
        var options=new SnapshotOptions(SnapshotFormat.Csv,';',',');
        Assert.Null(await preview.PreviewAsync(new("other","factory"),"connector","1.0","orders",input,options));
        Assert.Null(await preview.PreviewAsync(new("fixture","foreign-model"),"connector","1.0","orders",input,options));
        Assert.Equal(0,registry.Calls);
        Assert.True((await preview.PreviewAsync(new("fixture","factory"),"connector","1.0","orders",input,options))!.Accepted);
        registry.Mapping=registry.Mapping with {Stream=Stream with {TenantId="other"}};
        Assert.Null(await preview.PreviewAsync(new("fixture","factory"),"connector","1.0","orders",input,options));
    }
    private sealed class Registry(RegisteredMapping mapping):IAuthorizedMappingRegistry
    {
        public RegisteredMapping Mapping=mapping; public int Calls;
        public ValueTask<RegisteredMapping?> FindAsync(TenantActor actor,string modelId,string connectorId,string mappingVersion,string streamId,CancellationToken ct)
        { Calls++; return ValueTask.FromResult<RegisteredMapping?>(Mapping); }
    }
    private sealed class Accessor:IHttpContextAccessor {public HttpContext? HttpContext{get;set;}}
    private sealed class Directory:ITenantMembershipDirectory
    {
        public ValueTask<bool> IsActiveAsync(TenantActor actor,CancellationToken ct)=>ValueTask.FromResult(true);
        public ValueTask<bool> AllowsAsync(TenantActor actor,TenantPermission permission,string model,CancellationToken ct)=>ValueTask.FromResult(model=="factory"&&permission==TenantPermission.Write);
    }
    private sealed class Audit:ITenantDenialAudit {public ValueTask RecordAsync(TenantDenial denial,CancellationToken ct)=>ValueTask.CompletedTask;}
    private sealed class Authentication:IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext c,string? s)=>Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new("iss","https://issuer.example.test"),new("sub","tester"),new("tenant_id","fixture")],"Bearer")),"Bearer")));
        public Task ChallengeAsync(HttpContext c,string? s,AuthenticationProperties? p)=>throw new NotSupportedException();
        public Task ForbidAsync(HttpContext c,string? s,AuthenticationProperties? p)=>throw new NotSupportedException();
        public Task SignInAsync(HttpContext c,string? s,ClaimsPrincipal p,AuthenticationProperties? a)=>throw new NotSupportedException();
        public Task SignOutAsync(HttpContext c,string? s,AuthenticationProperties? p)=>throw new NotSupportedException();
    }
}
