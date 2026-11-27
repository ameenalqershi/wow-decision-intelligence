using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.Hosting;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Bounds;
using WowDi.Verification.Evidence;
using Xunit;

namespace WowDi.Tests;

public sealed class LocalEvidenceTests : IDisposable
{
    private readonly string root=Directory.CreateTempSubdirectory("wowdi-local-evidence-").FullName;
    private static readonly byte[] Facts="""{"available":"8"}"""u8.ToArray();
    private static readonly byte[] Catalog="""{"units":[{"symbol":"minute","dimension":"time","scale":"60"}],"currencies":["USD"],"calendars":["shift-a"]}"""u8.ToArray();
    private static readonly byte[] Settings="""{"deterministic":true,"seed":17}"""u8.ToArray();
    private static readonly SolverIdentity Solver=new("enumerated-test-fixture","1","managed");
    private static readonly SolverCapabilities Caps=new(Solver,["integer.linear"],[ScalarKind.Integer],null,null,1,false,true,true);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Fixture(string ext)=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory."+ext));
    private LocalWorkspace Create(string name="a",string tenant="fixture")=>LocalWorkspace.Create(Path.Combine(root,name),tenant);

    private static EvidenceBinding Register(LocalWorkspace w)
    {
        var d=UdirDocument.Read(Fixture("json"));
        var doc=UdirDocument.Create(d.Model with{TenantId=w.Identity.TenantId,Scenarios=[],Requires=["integer.linear"],InputDigests=[Hash(Facts),Hash(Catalog)]});
        var key=new GraphRevision(w.Identity.TenantId,doc.Model.ModelId,doc.Model.RevisionId);
        var source=Fixture("txt");
        w.SaveRevision(doc,DecisionGraphSnapshot.Create(key,[],[]),new Dictionary<string,ReadOnlyMemory<byte>>
        {{Hash(source),source},{Hash(Facts),Facts},{Hash(Catalog),Catalog}});
        return new(new(key.TenantId,key.ModelId,key.RevisionId,"run",Hash(doc.Write())),Hash(Facts),Hash(Catalog));
    }
    private static RunInvocation Invocation(string attempt="attempt")=>new(attempt,new string('a',64),"compiler/1","planner/1","test-enumeration",
        new string('b',64),"managed","test-fixture",4,1024L*1024*1024,1,128*1024*1024,30000,17,
        DateTimeOffset.UnixEpoch.AddDays(20000),DateTimeOffset.UnixEpoch.AddDays(20000).AddSeconds(1),true,100);
    // Test-only enumerated witness; this is not a native solver or actual worker invocation.
    private static SolverResult Witness(EvidenceBinding b,string value="56/3")=>new(b.Scope.TenantId,b.Scope.ModelId,b.Scope.RevisionId,b.Scope.RunId,
        Solver,MathematicalStatus.Feasible,Termination.Completed,ImmutableDictionary<string,string>.Empty.Add("x","8"),
        [new("revenue",value,null,"0","0")],null,new(null,null,null));
    private static EvidenceKey Capture(LocalEvidence e,EvidenceBinding b,string attempt="attempt",string? parent=null,string value="56/3",CancellationToken ct=default)
        =>e.Capture(b,Invocation(attempt),Catalog,Caps,Witness(b,value),Settings,parent,ct);
    private static void Sql(LocalWorkspace w,string file,string sql)
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(w.DirectoryPath,file),Pooling=false,ForeignKeys=true}.ToString());
        db.Open();using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ReopenRestoreAndPortableReplayPreserveEvidenceIdentity()
    {
        var a=Create();var b=Register(a);var e=new LocalEvidence(a);
        var key=Capture(e,b);Assert.Equal(key,Capture(e,b));
        var child=Capture(e,b,"next",key.EvidenceId);
        var exported=e.Export(key);
        Assert.Equal(EvidenceStrength.ExactEnumeration,e.Find(key)!.Assessment.Strength);
        Assert.Equal("56/3",e.Find(key)!.Assessment.Proof!.Optimum);
        a=LocalWorkspace.Open(a.DirectoryPath);
        using var host=LocalHost.Build(new(a.DirectoryPath));
        var reopened=host.Services.GetRequiredService<LocalEvidence>();
        Assert.Equal(key.EvidenceId,reopened.Replay(exported).EvidenceId);
        var copy=WorkspaceBackup.RestoreCopy(a,Path.Combine(root,"restored"));
        Assert.NotEqual(a.Identity.WorkspaceId,copy.Identity.WorkspaceId);
        var restored=new LocalEvidence(copy);
        Assert.Equal(exported.OriginalSha256,restored.Export(key).OriginalSha256);
        Assert.Equal(exported.Bytes.ToArray(),restored.Export(key).Bytes.ToArray());
        Assert.Equal(key.EvidenceId,restored.Find(child)!.Manifest.ParentEvidenceId);
        Assert.Equal(key.EvidenceId,restored.Replay(exported).EvidenceId);
        var mutated=exported.Bytes.ToArray();mutated[^1]^=1;
        Assert.Throws<ContractException>(()=>restored.Replay(exported with{Bytes=ImmutableArray.CreateRange(mutated)}));
        var foreign=Create("foreign","other");
        Assert.Equal("workspace.scope",Assert.Throws<ContractException>(()=>new LocalEvidence(foreign).Replay(exported)).Code);
    }

    [Fact]
    public void RejectedObservationRemainsRejectedAndAttemptConflictDoesNotReplaceIt()
    {
        var a=Create();var b=Register(a);var e=new LocalEvidence(a);
        var key=Capture(e,b,value:"18");
        Assert.True(e.Find(key)!.Assessment.Rejected);
        Assert.Throws<ContractException>(()=>Capture(e,b));
        Assert.True(e.Find(key)!.Assessment.Rejected);
        var copy=WorkspaceBackup.RestoreCopy(a,Path.Combine(root,"copy"));
        Assert.True(new LocalEvidence(copy).Find(key)!.Assessment.Rejected);
    }

    [Fact]
    public void MissingEvidenceDatabaseNeverSilentlyRecreated()
    {
        var a=Create();var b=Register(a);var e=new LocalEvidence(a);var key=Capture(e,b);
        File.Delete(Path.Combine(a.DirectoryPath,"evidence.db"));
        Assert.Equal("workspace.evidenceMissing",Assert.Throws<ContractException>(()=>LocalWorkspace.Open(a.DirectoryPath)).Code);
        Assert.Equal("workspace.evidenceMissing",Assert.Throws<ContractException>(()=>e.Find(key)).Code);
        Assert.Throws<ContractException>(()=>Capture(e,b,"other"));
        Assert.Throws<ContractException>(()=>WorkspaceBackup.RestoreCopy(a,Path.Combine(root,"copy")));
        Assert.False(File.Exists(Path.Combine(a.DirectoryPath,"evidence.db")));
        Assert.False(Directory.Exists(Path.Combine(root,"copy")));
    }

    [Fact]
    public void CatalogMustBeFrozenWithTheOriginalRevision()
    {
        var a=Create();var b=Register(a);var e=new LocalEvidence(a);
        var other=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(Catalog)+" ");
        var wrong=b with{CatalogSha256=Hash(other)};
        Assert.Equal("workspace.catalogNotFrozen",Assert.Throws<ContractException>(()=>
            e.Capture(wrong,Invocation(),other,Caps,Witness(wrong),Settings)).Code);
        Assert.NotNull(e.Find(Capture(e,b)));
    }

    [Fact]
    public void LegacyV2MigrationCreatesStoreAndRetainsRevision()
    {
        var a=Create();var b=Register(a);
        Sql(a,"workspace.db","UPDATE workspace_identity SET schema_version=2;");
        File.Delete(Path.Combine(a.DirectoryPath,"evidence.db"));
        a=LocalWorkspace.Open(a.DirectoryPath);
        Assert.Equal(LocalWorkspace.SchemaVersion,a.Identity.SchemaVersion);
        Assert.NotNull(a.FindRevision(b.Scope.ModelId,b.Scope.RevisionId));
        Assert.NotNull(new LocalEvidence(a).Find(Capture(new LocalEvidence(a),b)));
    }

    [Fact]
    public void CorruptEvidenceAndForeignRowsPreventBackupPublication()
    {
        var a=Create();var b=Register(a);var key=Capture(new LocalEvidence(a),b);
        Sql(a,"evidence.db","DROP TRIGGER evidence_no_update; UPDATE evidence SET settings=x'7b7d';");
        Assert.Throws<ContractException>(()=>WorkspaceBackup.RestoreCopy(a,Path.Combine(root,"corrupt")));
        Assert.False(Directory.Exists(Path.Combine(root,"corrupt")));
        Sql(a,"evidence.db","UPDATE evidence SET tenant='other';");
        Assert.Equal("workspace.tenantScope",Assert.Throws<ContractException>(()=>LocalWorkspace.Open(a.DirectoryPath)).Code);
        Assert.Equal("workspace.tenantScope",Assert.Throws<ContractException>(()=>new LocalEvidence(a).Find(key)).Code);
    }

    [Fact]
    public void ConcurrentExactCaptureAndCancelledBackupPreserveSingleAttempt()
    {
        var a=Create();var b=Register(a);
        var keys=Enumerable.Range(0,8).AsParallel().Select(_=>Capture(new LocalEvidence(LocalWorkspace.Open(a.DirectoryPath)),b)).ToArray();
        Assert.All(keys,k=>Assert.Equal(keys[0],k));
        using(var lease=new FileStream(Path.Combine(a.DirectoryPath,".workspace-write.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        using(var ct=new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            Assert.Throws<OperationCanceledException>(()=>WorkspaceBackup.RestoreCopy(a,Path.Combine(root,"cancelled"),ct.Token));
        Assert.False(Directory.Exists(Path.Combine(root,"cancelled")));
        Assert.NotNull(new LocalEvidence(a).Find(keys[0]));
    }

    public void Dispose()=>Directory.Delete(root,true);
}
