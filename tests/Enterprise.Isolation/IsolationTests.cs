using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Runtime.Governance;
using Xunit;

namespace WowDi.Enterprise.Isolation.Tests;

public sealed class IsolationTests
{
    private static WorkerLease Lease(byte[] input,string tenant="a",int threads=1)=>new(
        new(new(tenant,"model","rev","run"),new(threads,256*1024*1024,TimeSpan.FromSeconds(10)),Convert.ToHexStringLower(SHA256.HashData(input))),
        Guid.NewGuid(),1,TimeSpan.FromSeconds(9));
    private static ExtensionIdentity Identity()=>new("plugin","1.0",new string('a',64));
    private static ExtensionGrant Grant(ExtensionIdentity id)=>new("a","model",id,[ExtensionCapability.ReadFrozenInput],1,256*1024*1024,TimeSpan.FromSeconds(10));

    [Fact]
    public async Task GrantsAreExactAndUnknownCapabilitiesFailClosed()
    {
        var id=Identity();var directory=new DirectoryFixture(Grant(id));
        var admission=new ExtensionAdmission(directory);
        var lease=Lease([1]);
        await admission.AuthorizeAsync(lease,new(id,[ExtensionCapability.ReadFrozenInput]));
        async Task Reject(WorkerLease l,ExtensionInvocation i,string code)
            =>Assert.Equal(code,(await Assert.ThrowsAsync<ExtensionRejectedException>(()=>admission.AuthorizeAsync(l,i).AsTask())).Code);
        await Reject(Lease([1],"b"),new(id,[ExtensionCapability.ReadFrozenInput]),"extension.denied");
        await Reject(Lease([1],threads:2),new(id,[ExtensionCapability.ReadFrozenInput]),"extension.denied");
        await Reject(lease,new(id with{Version="2.0"},[]),"extension.denied");
        await Reject(lease,new(id with{PackageSha256=new string('b',64)},[]),"extension.denied");
        await Reject(lease,new(id,[ExtensionCapability.EphemeralScratch]),"extension.denied");
        await Reject(lease,new(id,[(ExtensionCapability)99]),"extension.request");
        await Reject(lease,new(id,[ExtensionCapability.ReadFrozenInput,ExtensionCapability.ReadFrozenInput]),"extension.request");
        directory.Grant=null;
        await Reject(lease,new(id,[]),"extension.denied");
    }

    [Fact]
    public async Task MissingSandboxNeverReturnsAnExecutablePlan()
    {
        var id=Identity();
        var sandbox=new LinuxExtensionSandbox(new("/missing/bwrap","/missing/root","/missing/bundles","/missing/staging"),
            new(new DirectoryFixture(Grant(id))));
        var failure=await Assert.ThrowsAsync<ExtensionRejectedException>(()=>sandbox.PrepareAsync(Lease([1]),new(id,[ExtensionCapability.ReadFrozenInput]),new(id,"plugin.dll"),new byte[]{1}));
        Assert.Equal(OperatingSystem.IsLinux()?"extension.sandboxUnavailable":"extension.platform",failure.Code);
    }

    [Fact]
    public async Task ExactPackageAndFrozenInputStagingRejectTamperingAndRevocation()
    {
        if(!OperatingSystem.IsLinux())return;
        var dir=System.IO.Directory.CreateTempSubdirectory("wowdi-isolation-");
        try
        {
            var root=System.IO.Directory.CreateDirectory(Path.Combine(dir.FullName,"runtime")).FullName;
            foreach(var m in new[]{"plugin","input","scratch","tmp","proc","dev","usr/share/dotnet"})
                System.IO.Directory.CreateDirectory(Path.Combine(root,m));
            File.WriteAllText(Path.Combine(root,"usr/share/dotnet/dotnet"),"fixture not executable");
            var bundles=System.IO.Directory.CreateDirectory(Path.Combine(dir.FullName,"bundles")).FullName;
            var bundle=System.IO.Directory.CreateDirectory(Path.Combine(bundles,"unregistered")).FullName;
            File.WriteAllText(Path.Combine(bundle,"plugin.dll"),"fixture package");
            var digest=await LinuxExtensionSandbox.PackageDigestAsync(bundle);
            var final=Path.Combine(bundles,digest);System.IO.Directory.Move(bundle,final);
            var staging=System.IO.Directory.CreateDirectory(Path.Combine(dir.FullName,"staging")).FullName;
            var id=Identity() with{PackageSha256=digest};
            var approval=new DirectoryFixture(Grant(id));
            // Only inspect arguments; /usr/bin/true is NOT a sandbox and is NEVER executed.
            var sandbox=new LinuxExtensionSandbox(new("/usr/bin/true",root,bundles,staging),new(approval));
            var bytes=Encoding.UTF8.GetBytes("synthetic fixture");
            var lease=Lease(bytes);var invocation=new ExtensionInvocation(id,[ExtensionCapability.ReadFrozenInput]);
            var registered=new RegisteredExtension(id,"plugin.dll");
            var prepared=await sandbox.PrepareAsync(lease,invocation,registered,bytes);
            Assert.Contains("--unshare-net",prepared.Program.Arguments);
            Assert.Contains("--disable-userns",prepared.Program.Arguments);
            Assert.Contains("--clearenv",prepared.Program.Arguments);
            Assert.Contains("/plugin/plugin.dll",prepared.Program.Arguments);
            Assert.Equal(bytes,File.ReadAllBytes(Path.Combine(prepared.Program.WorkingDirectory,"model.bin")));
            Assert.Throws<ExtensionRejectedException>(()=>prepared.ReleaseAfterConfirmedCleanup(false));
            Assert.True(System.IO.Directory.Exists(prepared.Program.WorkingDirectory));
            prepared.ReleaseAfterConfirmedCleanup(true);
            Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(staging));
            Assert.Equal("extension.inputDigest",(await Assert.ThrowsAsync<ExtensionRejectedException>(()=>sandbox.PrepareAsync(lease,invocation,registered,new byte[]{9}))).Code);
            approval.OnLookup=()=>{if(++approval.Calls==2)approval.Grant=null;};
            Assert.Equal("extension.denied",(await Assert.ThrowsAsync<ExtensionRejectedException>(()=>sandbox.PrepareAsync(lease,invocation,registered,bytes))).Code);
            Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(staging));
            approval.Grant=Grant(id);approval.OnLookup=null;
            File.AppendAllText(Path.Combine(final,"plugin.dll"),"tampered");
            Assert.Equal("extension.packageDigest",(await Assert.ThrowsAsync<ExtensionRejectedException>(()=>sandbox.PrepareAsync(lease,invocation,registered,bytes))).Code);
            File.CreateSymbolicLink(Path.Combine(final,"link.dll"),Path.Combine(final,"plugin.dll"));
            Assert.Equal("extension.packagePath",(await Assert.ThrowsAsync<ExtensionRejectedException>(()=>LinuxExtensionSandbox.PackageDigestAsync(final))).Code);
        }
        finally{dir.Delete(recursive:true);}
    }
    private sealed class DirectoryFixture(ExtensionGrant? grant):IExtensionApprovalDirectory
    {
        internal ExtensionGrant? Grant=grant;
        internal Action? OnLookup;
        internal int Calls;
        public ValueTask<ExtensionGrant?> FindAsync(string tenant,string model,ExtensionIdentity identity,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();OnLookup?.Invoke();return ValueTask.FromResult(Grant);}
    }
}
