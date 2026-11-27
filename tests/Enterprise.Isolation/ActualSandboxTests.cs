using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using Xunit;

namespace WowDi.Enterprise.Isolation.Tests;

[Trait("Category","OSIsolation")]
public sealed class ActualSandboxTests
{
    private static string Env(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException(name+" required.");
    private sealed class Approvals(ExtensionGrant grant):IExtensionApprovalDirectory
    {
        public ValueTask<ExtensionGrant?> FindAsync(string tenant,string model,ExtensionIdentity id,CancellationToken ct)
            =>ValueTask.FromResult<ExtensionGrant?>(grant);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualIsolationOfHostFilesNetworkAndWritableMounts(bool scratch)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var root=Directory.CreateTempSubdirectory("wowdi-e07-host-");
        try
        {
            var marker=Path.Combine(root.FullName,"host-only.txt");
            File.WriteAllText(marker,"synthetic other-tenant marker");
            var input=Encoding.UTF8.GetBytes($"probe:{port}:{marker}");
            var result=await Run(input,scratch,TimeSpan.FromSeconds(20));
            Assert.True(result.Termination==Termination.Completed,$"{result.Termination}; {result.Diagnostic}; exit={result.ExitCode}; peak={result.ScopePeakBytes}");
            Assert.True(result.CleanupConfirmed);
            using var response=JsonDocument.Parse(result.Candidate!);
            var value=response.RootElement;
            foreach(var name in new[]{"canReadHost","canWriteInput","canWritePlugin","inherited","connected"})
                Assert.False(value.GetProperty(name).GetBoolean(),name);
            Assert.Equal(scratch,value.GetProperty("canWriteScratch").GetBoolean());
            Assert.False(listener.Pending());
            Assert.Equal("synthetic other-tenant marker",File.ReadAllText(marker));
        }
        finally{root.Delete(recursive:true);}
    }

    [Theory]
    [InlineData("delay")]
    [InlineData("memory")]
    public async Task ActualSandboxEnforcesExpiryAndMemory(string mode)
    {
        var result=await Run(Encoding.UTF8.GetBytes(mode),false,TimeSpan.FromSeconds(mode=="delay"?3:20));
        Assert.True(result.CleanupConfirmed);
        Assert.Null(result.Candidate);
        if(mode=="delay")Assert.True(result.Termination==Termination.Timeout,$"{result.Termination}; {result.Diagnostic}; exit={result.ExitCode}");
        else
        {
            Assert.Equal(Termination.Failed,result.Termination);
            Assert.True(result.ScopePeakBytes>=128L*1024*1024,"The memory fixture must actually allocate memory, not simply fail at sandbox startup.");
        }
        Assert.True(result.ScopePeakBytes>0);
    }

    private static async Task<BoundaryResult> Run(byte[] input,bool scratch,TimeSpan deadline)
    {
        var root=Directory.CreateTempSubdirectory("wowdi-e07-");
        try
        {
            var bundles=Directory.CreateDirectory(Path.Combine(root.FullName,"bundles")).FullName;
            var staging=Directory.CreateDirectory(Path.Combine(root.FullName,"staging")).FullName;
            var bundle=Directory.CreateDirectory(Path.Combine(bundles,"incoming")).FullName;
            foreach(var file in Directory.EnumerateFiles(Env("WOWDI_TEST_PLUGIN_DIR")))
                File.Copy(file,Path.Combine(bundle,Path.GetFileName(file)));
            var digest=await LinuxExtensionSandbox.PackageDigestAsync(bundle);
            Directory.Move(bundle,Path.Combine(bundles,digest));
            var id=new ExtensionIdentity("isolation-fixture","1",digest);
            ImmutableArray<ExtensionCapability> caps=scratch?[ExtensionCapability.ReadFrozenInput,ExtensionCapability.EphemeralScratch]:[ExtensionCapability.ReadFrozenInput];
            const long memory=256L*1024*1024;
            var governor=new WorkerGovernor(new(1,memory,1,8),new Dictionary<string,TenantCapacity>{{"a",new(1,memory,1,4)}});
            var request=new WorkerRequest(new("a","model","revision","run"),new(1,memory,deadline),Convert.ToHexStringLower(SHA256.HashData(input)));
            Assert.Equal(Admission.Accepted,governor.Submit(request));
            var lease=governor.TryAcquire()!;
            var grant=new ExtensionGrant("a","model",id,caps,1,memory,TimeSpan.FromSeconds(30));
            var sandbox=new LinuxExtensionSandbox(new(Env("WOWDI_TEST_BWRAP"),Env("WOWDI_TEST_ROOTFS"),bundles,staging),new(new Approvals(grant)));
            var prepared=await sandbox.PrepareAsync(lease,new(id,caps),new(id,"WowDi.Enterprise.Isolation.Fixture.dll"),input);
            var boundary=new WorkerProcessBoundary(new LinuxCgroupScopeFactory(Env("WOWDI_TEST_CGROUP_ROOT")),
                new(Env("WOWDI_TEST_DOTNET"),[Env("WOWDI_TEST_BOOTSTRAP")],root.FullName));
            var (completion,result)=await new WorkerExecutor(governor,boundary).RunAsync(lease,prepared.Program);
            // Never clean a potentially live scope. Preserve staging for incident recovery.
            Assert.True(result.CleanupConfirmed,result.Diagnostic);
            prepared.ReleaseAfterConfirmedCleanup(result.CleanupConfirmed);
            Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
            Assert.Equal(0,governor.Snapshot().ReservedBytes);
            Assert.Equal(result.Termination==Termination.Completed,completion.CandidateEligible);
            root.Delete(recursive:true);
            return result;
        }
        catch
        {
            // Preserve diagnostic staging on an unconfirmed cleanup failure.
            throw;
        }
    }
}
