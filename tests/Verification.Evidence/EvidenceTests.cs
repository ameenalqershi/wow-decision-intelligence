using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.ModelRegistry;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Bounds;
using Xunit;

namespace WowDi.Verification.Evidence.Tests;

public sealed class EvidenceTests:IDisposable
{
    private readonly DirectoryInfo _directory=Directory.CreateTempSubdirectory("wowdi-evidence-");
    private readonly string _registryPath,_evidencePath;
    private readonly SqliteModelRegistry _registry;
    private readonly SqliteEvidenceStore _store;
    private static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"60"}],"currencies":["USD"],"calendars":["shift-a"]}""");
    private static readonly byte[] Facts=Encoding.UTF8.GetBytes("""{"available":"8"}""");
    private static readonly byte[] Settings=Encoding.UTF8.GetBytes("""{"deterministic":true,"seed":17}""");
    private static readonly SolverIdentity Solver=new("fixture","1.2.3","linux-x64");
    private static readonly SolverCapabilities Caps=new(Solver,["integer.linear"],[ScalarKind.Integer],null,null,2,false,true,true);
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));

    public EvidenceTests()
    {
        _registryPath=Path.Combine(_directory.FullName,"registry.sqlite");
        _evidencePath=Path.Combine(_directory.FullName,"evidence.sqlite");
        _registry=new(_registryPath);_store=new(_evidencePath,_registry);
    }
    private StoredRevision Register(string tenant="tenant-a")
    {
        var d=UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json")));
        var doc=UdirDocument.Create(d.Model with{TenantId=tenant,Scenarios=[],Requires=["integer.linear"],InputDigests=[Hash(Facts)]});
        var key=new GraphRevision(tenant,doc.Model.ModelId,doc.Model.RevisionId);
        var source=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.txt"));
        _registry.Save(tenant,doc,DecisionGraphSnapshot.Create(key,[],[]),new Dictionary<string,ReadOnlyMemory<byte>>
        {{Hash(Facts),Facts},{Hash(source),source}});
        return _registry.Find(tenant,key.ModelId,key.RevisionId)!;
    }
    private static EvidenceBinding Binding(StoredRevision revision,string run="run-1")=>
        new(new(revision.Document.Model.TenantId,revision.Document.Model.ModelId,revision.Document.Model.RevisionId,run,
            Hash(revision.Document.Write())),Hash(Facts),Hash(Catalog));
    private static RunInvocation Invocation(string attempt="attempt-1",bool enumerate=true)=>
        new(attempt,new string('a',64),"compiler/1","planner/1","integer-linear",new string('b',64),
            "linux-x64","fixture-machine",4,8L*1024*1024*1024,2,256*1024*1024,30000,17,
            DateTimeOffset.UnixEpoch.AddDays(20000),DateTimeOffset.UnixEpoch.AddDays(20000).AddSeconds(1),enumerate,100);
    private static SolverResult Candidate(EvidenceBinding b,string? x="8",string? value="56/3",MathematicalStatus status=MathematicalStatus.Optimal)=>
        new(b.Scope.TenantId,b.Scope.ModelId,b.Scope.RevisionId,b.Scope.RunId,Solver,status,Termination.Timeout,
            x is null?null:ImmutableDictionary<string,string>.Empty.Add("x",x),
            [new("revenue",value,x is null?null:new("20",Direction.Maximize,new(EvidenceSource.SolverAttestation,"fixture-bound")),"0","0")],
            new(EvidenceSource.SolverAttestation,"fixture-status"),new(100,75,null));
    private static EvidenceRecord Capture(StoredRevision r,string attempt="attempt-1",string? parent=null,byte[]? settings=null)
    {
        var b=Binding(r);return EvidenceRecord.Capture(r,b,Invocation(attempt),Catalog,Caps,Candidate(b),settings??Settings,parent);
    }
    private SqliteConnection Open(string? path=null)
    {
        var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path??_evidencePath,Pooling=false,ForeignKeys=true}.ToString());c.Open();return c;
    }
    private void Sql(string sql)
    {using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    private long Count()
    {using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM evidence";return (long)cmd.ExecuteScalar()!;}
    private static EvidenceRecord Replay(StoredRevision r,EvidenceRecord e,byte[]? manifest=null,string? expected=null,byte[]? assessment=null)=>
        EvidenceRecord.Replay(r,r.Document.Model.TenantId,expected??e.EvidenceId,manifest??e.ManifestBytes.ToArray(),
            e.CatalogBytes.ToArray(),e.CapabilitiesBytes.ToArray(),e.SolverResultBytes.ToArray(),
            assessment??e.AssessmentBytes.ToArray(),e.SettingsBytes.ToArray());

    [Fact]
    public void CaptureStoreReopenAndReplayPreserveExactLineage()
    {
        var revision=Register();var evidence=Capture(revision);
        Assert.Equal(MathematicalStatus.Optimal,evidence.Assessment.Status);
        Assert.Equal(EvidenceStrength.ExactEnumeration,evidence.Assessment.Strength);
        Assert.Equal("56/3",evidence.Assessment.Proof!.Optimum);
        Assert.Equal(2,evidence.Manifest.RevisionInputDigests.Length);
        Assert.Equal(Hash(Catalog),evidence.Manifest.Binding.CatalogSha256);
        Assert.Equal(Hash(Settings),evidence.Manifest.SettingsSha256);
        Assert.Equal("fixture-machine",evidence.Manifest.Invocation.HardwareClass);
        Assert.Equal(Solver,evidence.Assessment.Solver);
        Assert.Equal(Termination.Timeout,evidence.Assessment.Termination);
        Assert.Equal("0",evidence.Assessment.AbsoluteTolerance);
        Assert.Equal(Hash(evidence.ManifestBytes.ToArray()),evidence.EvidenceId);
        var audit=JsonNode.Parse(evidence.ExportAuditReport())!.AsObject();
        Assert.Equal("wowdi.evidence.audit",audit["contract"]!.GetValue<string>());
        var payload=audit["payload"]!;
        Assert.Equal(evidence.EvidenceId,payload["evidenceId"]!.GetValue<string>());
        Assert.Equal("1.2.3",payload["solver"]!["version"]!.GetValue<string>());
        Assert.Equal("Timeout",payload["termination"]!.GetValue<string>());
        Assert.Equal("ExactEnumeration",payload["strength"]!.GetValue<string>());
        Assert.Equal("fixture-machine",payload["manifest"]!["invocation"]!["hardwareClass"]!.GetValue<string>());
        Assert.Equal(13,payload["proof"]!["checked"]!.GetValue<int>());
        Assert.Null(payload["proof"]!["witness"]);
        var key=_store.Save("tenant-a",evidence);
        var reopened=new SqliteEvidenceStore(_evidencePath,new SqliteModelRegistry(_registryPath));
        var found=reopened.Find("tenant-a",key)!;
        Assert.Equal(evidence.EvidenceId,found.EvidenceId);
        Assert.Equal(evidence.AssessmentBytes.ToArray(),found.AssessmentBytes.ToArray());
        Assert.Equal(evidence.SettingsBytes.ToArray(),found.SettingsBytes.ToArray());
        Assert.Equal(evidence.EvidenceId,Replay(revision,evidence).EvidenceId);
        var child=Capture(revision,"attempt-2",evidence.EvidenceId);
        var childKey=_store.Save("tenant-a",child);
        Assert.Equal(key.EvidenceId,_store.Find("tenant-a",childKey)!.Manifest.ParentEvidenceId);
        Assert.Equal(2,Count());
    }

    [Fact]
    public void TenantAndParentScopeAreExplicitAndDoNotLeak()
    {
        var a=Register();var b=Register("المؤسسة-ب");
        var evidence=Capture(a);var key=_store.Save("tenant-a",evidence);
        Assert.Null(_store.Find("المؤسسة-ب",key with{TenantId="المؤسسة-ب"}));
        Assert.Equal("evidence.scope",Assert.Throws<ContractException>(()=>_store.Find("المؤسسة-ب",key)).Code);
        Assert.Equal("evidence.scope",Assert.Throws<ContractException>(()=>_store.Save("المؤسسة-ب",evidence)).Code);
        Assert.Equal("evidence.parentMissing",Assert.Throws<ContractException>(()=>_store.Save("المؤسسة-ب",Capture(b,parent:evidence.EvidenceId))).Code);
        var separate=_store.Save("المؤسسة-ب",Capture(b));
        Assert.NotEqual(key.EvidenceId,separate.EvidenceId);
        Assert.Equal("المؤسسة-ب",_store.Find("المؤسسة-ب",separate)!.Manifest.Binding.Scope.TenantId);
        Assert.Equal(2,Count());
    }

    [Fact]
    public async Task ConcurrentIdenticalAttemptsConvergeAndConflictingAttemptsNeverOverwrite()
    {
        var revision=Register();var e=Capture(revision);
        var keys=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Task.Run(()=>_store.Save("tenant-a",e))));
        Assert.All(keys,key=>Assert.Equal(keys[0],key));Assert.Equal(1,Count());
        var conflicting=Capture(revision,settings:Encoding.UTF8.GetBytes("""{"seed":18}"""));
        Assert.Equal("evidence.attemptConflict",Assert.Throws<ContractException>(()=>_store.Save("tenant-a",conflicting)).Code);
        var variants=new[]{Capture(revision,"attempt-2"),Capture(revision,"attempt-2",settings:Encoding.UTF8.GetBytes("""{"seed":19}"""))};
        var outcomes=await Task.WhenAll(variants.Select(record=>Task.Run(()=>
        {
            try{_store.Save("tenant-a",record);return true;}
            catch(ContractException x) when(x.Code=="evidence.attemptConflict"){return false;}
        })));
        Assert.Single(outcomes,x=>x);Assert.Equal(2,Count());
        Assert.Equal(e.EvidenceId,_store.Find("tenant-a",keys[0])!.EvidenceId);
    }

    [Fact]
    public void RehashedFalseAssessmentStillFailsMathematicalReplay()
    {
        var revision=Register();var e=Capture(revision);
        var node=JsonNode.Parse(e.AssessmentBytes.ToArray())!.AsObject();
        node["bound"]="99";
        var falseAssessment=Encoding.UTF8.GetBytes(node.ToJsonString());
        var manifest=JsonNode.Parse(e.ManifestBytes.ToArray())!.AsObject();
        manifest["payload"]!["assessmentSha256"]=Hash(falseAssessment);
        var changed=Encoding.UTF8.GetBytes(manifest.ToJsonString());
        Assert.Equal("evidence.digest",Assert.Throws<ContractException>(()=>Replay(revision,e,changed)).Code);
        Assert.Equal("evidence.assessmentMismatch",Assert.Throws<ContractException>(()=>Replay(revision,e,changed,Hash(changed),falseAssessment)).Code);
        var wrongCatalog=Binding(revision) with{CatalogSha256=new string('c',64)};
        Assert.Equal("evidence.catalogDigest",Assert.Throws<ContractException>(()=>EvidenceRecord.Capture(revision,wrongCatalog,Invocation(),Catalog,Caps,Candidate(wrongCatalog),Settings)).Code);
        var altered=revision with{Inputs=revision.Inputs.SetItem(Hash(Facts),ImmutableArray.Create<byte>(0,1))};
        Assert.Equal("evidence.lineage",Assert.Throws<ContractException>(()=>Replay(altered,e)).Code);
    }

    [Fact]
    public void ImmutableRowsAndCorruptArtifactsFailClosed()
    {
        var revision=Register();var e=Capture(revision);var key=_store.Save("tenant-a",e);
        Assert.Throws<SqliteException>(()=>Sql("UPDATE evidence SET settings=x'01'"));
        Assert.Throws<SqliteException>(()=>Sql("DELETE FROM evidence"));
        // Simulate privileged file/database corruption, not a permitted application action.
        Sql("DROP TRIGGER evidence_no_update; UPDATE evidence SET settings=x'01'");
        Assert.Equal("evidence.artifactDigest",Assert.Throws<ContractException>(()=>_store.Find("tenant-a",key)).Code);
    }

    [Fact]
    public void InsertFailureRollsBackAndUnknownOrRejectedEvidenceCannotBecomeVerified()
    {
        var revision=Register();var b=Binding(revision);
        var unknown=EvidenceRecord.Capture(revision,b,Invocation(enumerate:false),Catalog,Caps,
            Candidate(b,null,null,MathematicalStatus.Unknown),Settings);
        Assert.Equal(MathematicalStatus.Unknown,unknown.Assessment.Status);
        Assert.Null(unknown.Assessment.Bound);Assert.Null(unknown.Assessment.Proof);
        Sql("CREATE TRIGGER fixture_failure BEFORE INSERT ON evidence BEGIN SELECT RAISE(ABORT,'fixture'); END");
        Assert.Throws<SqliteException>(()=>_store.Save("tenant-a",unknown));
        Assert.Equal(0,Count());Sql("DROP TRIGGER fixture_failure");
        var k=_store.Save("tenant-a",unknown);
        Assert.Equal(MathematicalStatus.Unknown,_store.Find("tenant-a",k)!.Assessment.Status);
        var bad=EvidenceRecord.Capture(revision,b,Invocation("attempt-2"),Catalog,Caps,Candidate(b,"9","21"),Settings);
        Assert.True(bad.Assessment.Rejected);
        Assert.Equal(EvidenceStrength.None,bad.Assessment.Strength);
        var rejected=_store.Save("tenant-a",bad);
        Assert.True(_store.Find("tenant-a",rejected)!.Assessment.Rejected);
    }

    [Fact]
    public void QuiescedSqliteBackupRestoreAndStoreIdentity()
    {
        var revision=Register();var e=Capture(revision);var key=_store.Save("tenant-a",e);
        var restoredRegistry=Path.Combine(_directory.FullName,"restored-registry.sqlite");
        var restoredEvidence=Path.Combine(_directory.FullName,"restored-evidence.sqlite");
        void Backup(string source,string destination){using var src=Open(source);using var dst=Open(destination);src.BackupDatabase(dst);}
        Backup(_registryPath,restoredRegistry);Backup(_evidencePath,restoredEvidence);
        var recovered=new SqliteEvidenceStore(restoredEvidence,new SqliteModelRegistry(restoredRegistry));
        Assert.Equal(e.EvidenceId,recovered.Find("tenant-a",key)!.EvidenceId);
        Assert.Equal(key,recovered.Save("tenant-a",e));
        Assert.Equal("evidence.storeIdentity",Assert.Throws<ContractException>(()=>new SqliteEvidenceStore(_registryPath,_registry)).Code);
        Assert.NotNull(_registry.Find("tenant-a",key.ModelId,key.RevisionId));
        Sql("PRAGMA user_version=999");
        Assert.Equal("evidence.storeIdentity",Assert.Throws<ContractException>(()=>new SqliteEvidenceStore(_evidencePath,_registry)).Code);
    }

    [Fact]
    public void PortablePackageIsDeterministicBoundedAndRequiresAuthoritativeScope()
    {
        var revision=Register();var e=Capture(revision);
        var bytes=EvidencePackage.Export(e);var digest=Hash(bytes);
        Assert.NotEqual(e.EvidenceId,digest);
        Assert.Equal(bytes,EvidencePackage.Export(Capture(revision)));
        var restored=EvidencePackage.Replay(revision,"tenant-a",e.EvidenceId,digest,bytes);
        Assert.Equal(e.AssessmentBytes.ToArray(),restored.AssessmentBytes.ToArray());
        Assert.Equal(e.EvidenceId,restored.EvidenceId);
        Assert.Equal("evidence.scopeOrVersion",Assert.Throws<ContractException>(()=>EvidencePackage.Replay(revision,"tenant-b",e.EvidenceId,digest,bytes)).Code);
        var corrupted=bytes.ToArray();corrupted[^1]^=1;
        Assert.Equal("evidence.packageDigest",Assert.Throws<ContractException>(()=>EvidencePackage.Replay(revision,"tenant-a",e.EvidenceId,digest,corrupted)).Code);
        var trailing=bytes.Append((byte)0).ToArray();
        Assert.Equal("evidence.packageFraming",Assert.Throws<ContractException>(()=>EvidencePackage.Replay(revision,"tenant-a",e.EvidenceId,Hash(trailing),trailing)).Code);
        var badLength=bytes.ToArray();System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(badLength.AsSpan(8,4),int.MaxValue);
        Assert.Equal("evidence.packageFraming",Assert.Throws<ContractException>(()=>EvidencePackage.Replay(revision,"tenant-a",e.EvidenceId,Hash(badLength),badLength)).Code);
        var truncated=bytes[..^1];
        Assert.Equal("evidence.packageFraming",Assert.Throws<ContractException>(()=>EvidencePackage.Replay(revision,"tenant-a",e.EvidenceId,Hash(truncated),truncated)).Code);
    }

    public void Dispose()=>_directory.Delete(recursive:true);
}
