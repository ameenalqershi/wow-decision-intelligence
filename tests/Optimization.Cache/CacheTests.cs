using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.DecisionGraph;
using WowDi.ModelRegistry;
using WowDi.Optimization.Fingerprints;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Bounds;
using WowDi.Verification.Evidence;
using Xunit;

namespace WowDi.Optimization.Cache.Tests;
public sealed class CacheTests:IDisposable
{
    private readonly DirectoryInfo directory=Directory.CreateTempSubdirectory("wowdi-cache-");
    private readonly SqliteModelRegistry registry;
    private static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"60"}],"currencies":["USD"],"calendars":["shift-a"]}""");
    private static readonly byte[] Facts=Encoding.UTF8.GetBytes("""{"available":"8"}""");
    private static readonly byte[] Settings=Encoding.UTF8.GetBytes("""{"threads":1}""");
    private static readonly SolverIdentity Solver=new("fixture","1","linux-x64");
    private static readonly SolverCapabilities Caps=new(Solver,["integer.linear"],[ScalarKind.Integer],null,null,2,false,true,true);
    public CacheTests()=>registry=new(Path.Combine(directory.FullName,"models.sqlite"));
    public void Dispose()=>directory.Delete(true);
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));

    private StoredRevision Register(string tenant="tenant",string revision="r1",Func<Model,Model>? mutate=null,byte[]? facts=null,byte[]? catalog=null)
    {
        facts??=Facts;catalog??=Catalog;
        var source=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.txt"));
        var original=UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"))).Model;
        var m=original with{TenantId=tenant,RevisionId=revision,Scenarios=[],Requires=["integer.linear"],
            InputDigests=[Hash(facts),Hash(catalog)],Objectives=[original.Objectives[0] with{Weight="1",Tolerance="0"}]};
        var doc=UdirDocument.Create(mutate?.Invoke(m)??m);
        var graph=DecisionGraphSnapshot.Create(new(tenant,m.ModelId,revision),[],[]);
        registry.Save(tenant,doc,graph,new Dictionary<string,ReadOnlyMemory<byte>>{{Hash(source),source},{Hash(facts),facts},{Hash(catalog),catalog}});
        return registry.Find(tenant,m.ModelId,revision)!;
    }
    private static (CacheIdentity Id,FingerprintContext Context,byte[] Package,EvidenceRecord Record) Evidence(StoredRevision r,string run="run",string attempt="attempt",bool enumerate=true,bool forged=false)
    {
        var m=r.Document.Model;
        var b=new EvidenceBinding(new(m.TenantId,m.ModelId,m.RevisionId,run,Hash(r.Document.Write())),Hash(Facts),Hash(Catalog));
        var i=new RunInvocation(attempt,new string('a',64),"compiler/1","planner/1","integer-linear",new string('b',64),
            "linux-x64","fixture",4,1024L*1024*1024,1,64*1024*1024,1000,17,
            DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch.AddSeconds(1),enumerate,100);
        var result=new SolverResult(m.TenantId,m.ModelId,m.RevisionId,run,Solver,MathematicalStatus.Optimal,Termination.Timeout,
            ImmutableDictionary<string,string>.Empty.Add("x","8"),[new("revenue",forged?"7":"56/3",new("20",Direction.Maximize,new(EvidenceSource.SolverAttestation,"fixture-bound")),"0","0")],
            new(EvidenceSource.SolverAttestation,"fixture-status"),new(10,5,null));
        var e=EvidenceRecord.Capture(r,b,i,Catalog,Caps,result,Settings);
        var p=EvidencePackage.Export(e);
        var c=new FingerprintContext(i.CompilerVersion,new string('c',64),i.PlannerVersion,e.Manifest.VerifierProfile,
            Solver.SolverId,Solver.Version,Solver.RuntimeId,i.AdapterPackageSha256,i.ProfileId,b.CatalogSha256,b.FactsSha256,e.CapabilitiesBytes.ToArray(),e.SettingsBytes.ToArray());
        return(new(b,i,e.EvidenceId,Hash(p)),c,p,e);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactReplayPreservesStrengthWithoutCrossRunTransfer(bool enumerate)
    {
        var r=Register();var e=Evidence(r,enumerate:enumerate);
        var cache=new ExactEvidenceCache(registry);
        Assert.Equal(CacheDisposition.Stored,cache.Remember("tenant",e.Id,e.Context,e.Package));
        Assert.Equal(CacheDisposition.AlreadyStored,cache.Remember("tenant",e.Id,e.Context,e.Package));
        var hit=cache.Read("tenant",e.Id,e.Context);
        Assert.Equal(CacheDisposition.Hit,hit.Disposition);
        Assert.Equal(e.Record.EvidenceId,hit.Record!.EvidenceId);
        Assert.Equal(e.Record.AssessmentBytes.ToArray(),hit.Record.AssessmentBytes.ToArray());
        Assert.Equal(enumerate?EvidenceStrength.ExactEnumeration:EvidenceStrength.IndependentlyVerifiedPrimal,hit.Record.Assessment.Strength);
        Assert.Equal(enumerate?EvidenceStrength.ExactEnumeration:EvidenceStrength.SolverAttestation,hit.Record.Assessment.BoundStrength);
        var newer=Evidence(r,run:"new-run");
        Assert.Equal(CacheDisposition.Miss,cache.Read("tenant",newer.Id,newer.Context).Disposition);
        var nextAttempt=Evidence(r,attempt:"attempt2");
        Assert.Equal(CacheDisposition.Miss,cache.Read("tenant",nextAttempt.Id,nextAttempt.Context).Disposition);
    }
    [Fact]
    public void ChangedUnitsGoalsInputsAndVersionsAreMisses()
    {
        var r=Register();var e=Evidence(r);var cache=new ExactEvidenceCache(registry);
        Assert.Equal(CacheDisposition.Stored,cache.Remember("tenant",e.Id,e.Context,e.Package));
        var versions=new[]{e.Context with{AdapterVersion="2"},e.Context with{CompilerBuildSha256=new string('d',64)},
            e.Context with{Settings="""{"threads":2}"""u8.ToArray()},e.Context with{VerifierVersion="future"},e.Context with{CatalogSha256=new string('f',64)}};
        foreach(var c in versions)Assert.Equal(CacheDisposition.Miss,cache.Read("tenant",e.Id,c).Disposition);
        var changedFacts=Encoding.UTF8.GetBytes("""{"available":"7"}""");
        var changedCatalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":["USD"],"calendars":["shift-a"]}""");
        var revisions=new[]{Register(revision:"units",catalog:changedCatalog),
            Register(revision:"goal",mutate:m=>m with{Objectives=[m.Objectives[0] with{Direction=Direction.Minimize}]}),
            Register(revision:"data",facts:changedFacts)};
        foreach(var rev in revisions)
        {
            var scope=e.Id.Binding.Scope with{RevisionId=rev.Document.Model.RevisionId,ModelSha256=Hash(rev.Document.Write())};
            var id=e.Id with{Binding=e.Id.Binding with{Scope=scope}};
            Assert.Equal(CacheDisposition.Miss,cache.Read("tenant",id,e.Context).Disposition);
            Assert.Equal(CacheDisposition.Rejected,cache.Remember("tenant",id,e.Context,e.Package));
        }
        Assert.Equal(CacheDisposition.Hit,cache.Read("tenant",e.Id,e.Context).Disposition);
    }
    [Fact]
    public void ForgedPackageAndRejectedObservationCannotBeAdmitted()
    {
        var r=Register();var e=Evidence(r);var cache=new ExactEvidenceCache(registry);
        var bad=e.Package.ToArray();bad[^1]^=1;
        Assert.Equal(CacheDisposition.Rejected,cache.Remember("tenant",e.Id,e.Context,bad));
        Assert.Equal(CacheDisposition.Rejected,cache.Remember("tenant",e.Id with{PackageSha256=Hash(bad)},e.Context,bad));
        var forged=Evidence(r,forged:true);Assert.True(forged.Record.Assessment.Rejected);
        Assert.Equal(CacheDisposition.Rejected,cache.Remember("tenant",forged.Id,forged.Context,forged.Package));
        Assert.Equal(0,cache.Count);
        using var c=new CancellationTokenSource();c.Cancel();
        Assert.Throws<OperationCanceledException>(()=>cache.Remember("tenant",e.Id,e.Context,e.Package,c.Token));
    }
    [Fact]
    public async Task TenantIsolationConcurrentReplayAndBoundedEviction()
    {
        var r=Register();var e=Evidence(r);var cache=new ExactEvidenceCache(registry,2);
        var statuses=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>cache.Remember("tenant",e.Id,e.Context,e.Package))));
        Assert.Single(statuses,s=>s==CacheDisposition.Stored);
        Assert.All(statuses,s=>Assert.Contains(s,new[]{CacheDisposition.Stored,CacheDisposition.AlreadyStored}));
        Assert.Equal(CacheDisposition.Miss,cache.Read("other",e.Id,e.Context).Disposition);
        Assert.Equal(CacheDisposition.Rejected,cache.Remember("other",e.Id,e.Context,e.Package));
        var second=Evidence(r,run:"second");var third=Evidence(r,run:"third");
        Assert.Equal(CacheDisposition.Stored,cache.Remember("tenant",second.Id,second.Context,second.Package));
        Assert.Equal(CacheDisposition.Hit,cache.Read("tenant",e.Id,e.Context).Disposition);
        Assert.Equal(CacheDisposition.Stored,cache.Remember("tenant",third.Id,third.Context,third.Package));
        Assert.Equal(CacheDisposition.Miss,cache.Read("tenant",second.Id,second.Context).Disposition);
        Assert.Equal(2,cache.Count);
        var reads=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>cache.Read("tenant",e.Id,e.Context))));
        Assert.All(reads,r=>Assert.Equal(CacheDisposition.Hit,r.Disposition));
        Assert.Equal(2,cache.InvalidateTenant("tenant"));
        Assert.Equal(CacheDisposition.Miss,cache.Read("tenant",e.Id,e.Context).Disposition);
    }
}
