using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Runtime.Governance;

namespace WowDi.Enterprise.Isolation;

/// <summary>Trusted deployment paths only. Root filesystem is an immutable, audited minimal runtime image,
/// not the host filesystem. Bundle store/staging root must not be writable by tenant or extension code.</summary>
public sealed record LinuxSandboxConfiguration(string BubblewrapExecutable,string RuntimeRoot,string BundleRoot,string StagingRoot);
public sealed record RegisteredExtension(ExtensionIdentity Identity,string EntryAssembly);

public sealed class LinuxExtensionSandbox(LinuxSandboxConfiguration configuration,ExtensionAdmission admission)
{
    public const int MaximumInputBytes=1024*1024;
    public const int MaximumPackageFiles=2048;
    public const long MaximumPackageBytes=256L*1024*1024;

    // A trusted host must invoke only through its S07 governor, never directly start the program.
    // The host owns staging lifetime and may release it only after verified scope cleanup.
    public async Task<PreparedExtension> PrepareAsync(WorkerLease lease,ExtensionInvocation invocation,RegisteredExtension registered,
        ReadOnlyMemory<byte> frozenInput,CancellationToken ct=default)
    {
        await admission.AuthorizeAsync(lease,invocation,ct);
        if(!OperatingSystem.IsLinux())throw new ExtensionRejectedException("extension.platform");
        if(registered.Identity!=invocation.Extension||!ExtensionAdmission.Token(registered.EntryAssembly)||
            !registered.EntryAssembly.EndsWith(".dll",StringComparison.Ordinal))
            throw new ExtensionRejectedException("extension.registration");
        var binary=Path.GetFullPath(configuration.BubblewrapExecutable);
        if(!Path.IsPathFullyQualified(configuration.BubblewrapExecutable)||!File.Exists(binary)||
            (File.GetUnixFileMode(binary)&(UnixFileMode.GroupWrite|UnixFileMode.OtherWrite))!=0)
            throw new ExtensionRejectedException("extension.sandboxUnavailable");
        var root=TrustedDirectory(configuration.RuntimeRoot);
        var bundles=TrustedDirectory(configuration.BundleRoot);
        var staging=TrustedDirectory(configuration.StagingRoot);
        if(Overlaps(root,bundles)||Overlaps(root,staging)||Overlaps(bundles,staging))
            throw new ExtensionRejectedException("extension.deployment");
        foreach(var mount in new[]{"plugin","input","scratch","tmp","proc","dev"})
            if(!Directory.Exists(Path.Combine(root,mount))||File.GetAttributes(Path.Combine(root,mount)).HasFlag(FileAttributes.ReparsePoint))
                throw new ExtensionRejectedException("extension.runtimeLayout");
        if(new[]{"plugin","input","scratch","tmp"}.Any(m=>Directory.EnumerateFileSystemEntries(Path.Combine(root,m)).Any())||
            !File.Exists(Path.Combine(root,"usr/share/dotnet/dotnet")))
            throw new ExtensionRejectedException("extension.runtimeLayout");
        // Resolve only content-addressed bundle identities in host-controlled storage.
        var bundle=TrustedDirectory(Path.Combine(bundles,registered.Identity.PackageSha256));
        if(!File.Exists(Path.Combine(bundle,registered.EntryAssembly))||
            await PackageDigestAsync(bundle,ct)!=registered.Identity.PackageSha256)
            throw new ExtensionRejectedException("extension.packageDigest");
        var read=invocation.RequestedCapabilities.Contains(ExtensionCapability.ReadFrozenInput);
        if(frozenInput.Length>MaximumInputBytes||read&&frozenInput.IsEmpty||!read&&!frozenInput.IsEmpty)
            throw new ExtensionRejectedException("extension.input");
        var bytes=frozenInput.ToArray();
        if(read&&Convert.ToHexStringLower(SHA256.HashData(bytes))!=lease.Request.FrozenInputSha256)
            throw new ExtensionRejectedException("extension.inputDigest");
        ct.ThrowIfCancellationRequested();
        var temporary=Path.Combine(staging,"run-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            if(read)
            {
                var file=Path.Combine(temporary,"model.bin");
                await using var stream=new FileStream(file,new FileStreamOptions{Access=FileAccess.Write,Mode=FileMode.CreateNew,
                    Share=FileShare.None,Options=FileOptions.Asynchronous,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});
                await stream.WriteAsync(bytes,ct);
            }
            // No network, host credentials, arbitrary host mounts or arbitrary command line.
            // Mount only the approved immutable runtime, exact package and staged input.
            // Empty scratch is explicit; runtime temp is always bounded by S07 cgroup memory.
            var args=ImmutableArray.CreateBuilder<string>();
            args.AddRange(new[]{"--unshare-all","--unshare-user","--unshare-net","--disable-userns","--die-with-parent",
                "--new-session","--cap-drop","ALL","--clearenv","--ro-bind",root,"/",
                "--proc","/proc","--dev","/dev","--ro-bind",bundle,"/plugin",
                "--size","16777216","--tmpfs","/tmp"});
            if(read)args.AddRange(new[]{"--ro-bind",temporary,"/input"});
            if(invocation.RequestedCapabilities.Contains(ExtensionCapability.EphemeralScratch))
                args.AddRange(new[]{"--size","16777216","--tmpfs","/scratch"});
            args.AddRange(new[]{"--setenv","HOME","/scratch","--setenv","TMPDIR","/tmp",
                "--setenv","DOTNET_ROOT","/usr/share/dotnet","--setenv","DOTNET_EnableDiagnostics","0",
                "--chdir","/plugin","--","/usr/share/dotnet/dotnet","/plugin/"+registered.EntryAssembly});
            // Recheck live authorization after staging. Caller still owns durable lease and publication check.
            await admission.AuthorizeAsync(lease,invocation,ct);
            return new PreparedExtension(new WorkerProgram(binary,args.ToImmutable(),temporary),temporary);
        }
        catch
        {
            Directory.Delete(temporary,recursive:true);
            throw;
        }
    }

    private static string TrustedDirectory(string path)
    {
        if(!Path.IsPathFullyQualified(path))throw new ExtensionRejectedException("extension.deployment");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(full=="/"||!Directory.Exists(full))throw new ExtensionRejectedException("extension.deployment");
        // Reject symlink/reparse traversal of configured bind roots; deployment must protect every ancestor.
        for(var current=new DirectoryInfo(full);current is not null;current=current.Parent)
            if(current.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new ExtensionRejectedException("extension.deployment");
        return full;
    }
    private static bool Overlaps(string a,string b)=>a==b||a.StartsWith(b+"/",StringComparison.Ordinal)||b.StartsWith(a+"/",StringComparison.Ordinal);
    // Stable bundle digest: sorted ordinal relative UTF-8 paths, length prefix, SHA-256 of exact bytes.
    // Registry/import must publish this digest after separately validating provenance (SEC02).
    public static async Task<string> PackageDigestAsync(string root,CancellationToken ct=default)
    {
        root=TrustedDirectory(root);
        var files=new List<string>();
        var dirs=new Stack<string>();dirs.Push(root);
        int entries=0;
        while(dirs.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if(++entries>MaximumPackageFiles*2)throw new ExtensionRejectedException("extension.packageLimit");
                var attrs=File.GetAttributes(entry);
                if(attrs.HasFlag(FileAttributes.ReparsePoint)||attrs.HasFlag(FileAttributes.Device))
                    throw new ExtensionRejectedException("extension.packagePath");
                var name=Path.GetFileName(entry);
                if(!ExtensionAdmission.Token(name))throw new ExtensionRejectedException("extension.packagePath");
                if(attrs.HasFlag(FileAttributes.Directory))dirs.Push(entry);
                else files.Add(entry);
                if(files.Count>MaximumPackageFiles)throw new ExtensionRejectedException("extension.packageLimit");
            }
        }
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("wowdi.extension.bundle/1\n"u8);
        long total=0;
        foreach(var file in files.OrderBy(x=>Path.GetRelativePath(root,x),StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var length=LinuxFileIdentity.RequireRegularFile(file);
            if(length<0||length>MaximumPackageBytes-total)throw new ExtensionRejectedException("extension.packageLimit");
            total+=length;
            var path=Encoding.UTF8.GetBytes(Path.GetRelativePath(root,file).Replace(Path.DirectorySeparatorChar,'/'));
            var framing=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(framing,path.Length);
            hash.AppendData(framing);hash.AppendData(path);
            await using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,8192,FileOptions.Asynchronous);
            using var fileHash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer=new byte[8192];long read=0;
            while(true)
            {
                var n=await stream.ReadAsync(buffer,ct);if(n==0)break;
                read+=n;if(read>length)throw new ExtensionRejectedException("extension.packageChanged");
                fileHash.AppendData(buffer.AsSpan(0,n));
            }
            var digest=fileHash.GetHashAndReset();
            if(read!=length)throw new ExtensionRejectedException("extension.packageChanged");
            hash.AppendData(digest);
        }
        if(files.Count==0)throw new ExtensionRejectedException("extension.packageEmpty");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed class PreparedExtension
{
    internal PreparedExtension(WorkerProgram program,string temporaryDirectory)
        =>(Program,TemporaryDirectory)=(program,temporaryDirectory);
    public WorkerProgram Program{get;}
    private string TemporaryDirectory{get;}
    // Do not release staging while a quarantined execution might still hold it.
    public void ReleaseAfterConfirmedCleanup(bool cleanupConfirmed)
    {
        if(!cleanupConfirmed)throw new ExtensionRejectedException("extension.cleanupUnconfirmed");
        if(Directory.Exists(TemporaryDirectory))Directory.Delete(TemporaryDirectory,recursive:true);
    }
}
