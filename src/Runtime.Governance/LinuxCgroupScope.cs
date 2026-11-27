using System.Diagnostics;
using System.Globalization;

namespace WowDi.Runtime.Governance;

/// <summary>Linux cgroup v2 containment for a deployment-provisioned delegated subtree.
/// The host must keep that subtree private from the adapter identity; E07 owns hostile-code isolation.
/// No system-wide settings or controller delegation are modified by this class.</summary>
public sealed class LinuxCgroupScopeFactory(string delegatedRoot,int maximumProcesses=64):IWorkerProcessScopeFactory
{
    public ValueTask<IWorkerProcessScope> CreateAsync(WorkerBudget budget,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("Linux cgroup v2 is required.");
        if(!Path.IsPathFullyQualified(delegatedRoot)||maximumProcesses is <2 or >4096||
            budget.Threads<=0||budget.MemoryBytes<=0)throw new ArgumentException("Invalid cgroup configuration.");
        var root=Path.GetFullPath(delegatedRoot);
        // Only a configured cgroup mount location, not an arbitrary data directory.
        if(!root.StartsWith("/sys/fs/cgroup/",StringComparison.Ordinal)||
            !File.Exists(Path.Combine(root,"cgroup.controllers")))throw new InvalidOperationException("No delegated cgroup root.");
        var enabled=File.ReadAllText(Path.Combine(root,"cgroup.subtree_control"))
            .Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if(!new[]{"cpu","memory","pids"}.All(enabled.Contains))
            throw new InvalidOperationException("Required cgroup controllers are not delegated.");
        var path=Path.Combine(root,"wowdi-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            // Kernel quota caps aggregate CPU time at the requested number of core-equivalents.
            Write(path,"cpu.max",FormattableString.Invariant($"{checked((long)budget.Threads*100000)} 100000"));
            Write(path,"memory.max",budget.MemoryBytes.ToString(CultureInfo.InvariantCulture));
            Write(path,"memory.swap.max","0");
            Write(path,"memory.oom.group","1");
            Write(path,"pids.max",maximumProcesses.ToString(CultureInfo.InvariantCulture));
            foreach(var name in new[]{"cgroup.procs","cgroup.kill","cgroup.events","cpu.stat","memory.peak"})
                if(!File.Exists(Path.Combine(path,name)))throw new InvalidOperationException("Required cgroup accounting is unavailable.");
            return ValueTask.FromResult<IWorkerProcessScope>(new Scope(path));
        }
        catch
        {
            Directory.Delete(path);
            throw;
        }
    }

    private static void Write(string path,string name,string value)
    {
        // Kernel pseudo-files do not need file creation/truncation.
        using var file=new FileStream(Path.Combine(path,name),FileMode.Open,FileAccess.Write,FileShare.ReadWrite);
        file.Write(System.Text.Encoding.ASCII.GetBytes(value));
    }
    private sealed class Scope(string path):IWorkerProcessScope
    {
        private bool _attached,_empty,_disposed;
        public ValueTask AttachAsync(Process bootstrap,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(_attached||_disposed||bootstrap.HasExited)throw new InvalidOperationException("Invalid worker attachment.");
            var id=bootstrap.Id.ToString(CultureInfo.InvariantCulture);
            Write(path,"cgroup.procs",id);
            if(!File.ReadLines(Path.Combine(path,"cgroup.procs")).Contains(id,StringComparer.Ordinal))
                throw new InvalidOperationException("Worker attachment not confirmed.");
            _attached=true;
            return ValueTask.CompletedTask;
        }
        public async ValueTask<ScopeCompletion> CompleteAsync(CancellationToken ct)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(Scope));
            // Kernel-wide cgroup.kill avoids PID reuse and descendant enumeration races.
            var residual=Populated();
            if(residual)Write(path,"cgroup.kill","1");
            while(Populated())
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(25,ct);
            }
            _empty=true;
            var cpu=ReadStat("cpu.stat","usage_usec")/1000;
            var peak=long.Parse(File.ReadAllText(Path.Combine(path,"memory.peak")).Trim(),CultureInfo.InvariantCulture);
            return new(true,residual,cpu,peak,"cgroup-v2-charged-memory");
        }
        private bool Populated()=>ReadStat("cgroup.events","populated")!=0;
        private long ReadStat(string name,string key)
        {
            foreach(var line in File.ReadLines(Path.Combine(path,name)))
            {
                var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length==2&&parts[0]==key)return long.Parse(parts[1],CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("Missing cgroup statistic.");
        }
        public ValueTask DisposeAsync()
        {
            if(_disposed)return ValueTask.CompletedTask;
            // Leave an unconfirmed group for host incident recovery; never silently release its reservation.
            if(_empty)Directory.Delete(path);
            _disposed=true;
            return ValueTask.CompletedTask;
        }
    }
}
