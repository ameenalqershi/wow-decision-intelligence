using WowDi.Solver.Contracts;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WowDi.Runtime.Governance.Tests")]

namespace WowDi.Runtime.Governance;

public sealed record WorkerRunKey(string TenantId,string ModelId,string RevisionId,string RunId);
public sealed record WorkerBudget(int Threads,long MemoryBytes,TimeSpan Deadline);
public sealed record WorkerRequest(WorkerRunKey Key,WorkerBudget Budget,string FrozenInputSha256);
public sealed record WorkerCapacity(int Threads,long MemoryBytes,int MaxRunning,int MaxTracked);
public sealed record TenantCapacity(int Threads,long MemoryBytes,int MaxRunning,int MaxQueued);
public sealed record WorkerLease(WorkerRequest Request,Guid Epoch,long Fence,TimeSpan Remaining);
public enum Admission { Accepted, Duplicate, Conflict, Full, UnknownTenant, Invalid }
public enum RunState { Queued, Running, Revoked, Completed, Failed, Cancelled, TimedOut, Quarantined }
public sealed record GovernorSnapshot(int Running,int ReservedThreads,long ReservedBytes,int Queued,int Tracked);
public sealed record WorkerCompletion(bool Accepted,bool CandidateEligible,RunState State);

/// <summary>One host's bounded reservations, round-robin tenant queues and local attempt fence.
/// Durable authority and cross-host publication belong to the application lease store.</summary>
public sealed class WorkerGovernor
{
    private sealed class Entry(WorkerRequest request,long started)
    {
        internal WorkerRequest Request=request;
        internal long Started=started;
        internal long Fence;
        internal bool ExecutionStarted;
        internal RunState State=RunState.Queued;
        internal RunState Revocation=RunState.Cancelled;
    }
    private sealed class Tenant(TenantCapacity capacity)
    {
        internal TenantCapacity Capacity=capacity;
        internal LinkedList<Entry> Queue=[];
        internal int Threads,Running;
        internal long Bytes;
    }
    private readonly object _gate=new();
    private readonly WorkerCapacity _capacity;
    private readonly Dictionary<string,Tenant> _tenants;
    private readonly LinkedList<string> _rotation=[];
    private readonly Dictionary<WorkerRunKey,Entry> _entries=[];
    private readonly TimeProvider _clock;
    private readonly Guid _epoch=Guid.NewGuid();
    private long _fence;
    private int _running,_threads;
    private long _bytes;

    public WorkerGovernor(WorkerCapacity capacity,IReadOnlyDictionary<string,TenantCapacity> tenants,TimeProvider? clock=null)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(tenants);
        if(capacity.Threads<=0||capacity.MemoryBytes<=0||capacity.MaxRunning<=0||capacity.MaxTracked<capacity.MaxRunning||
            capacity.MaxTracked>100000||tenants.Count is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity=capacity;
        _clock=clock??TimeProvider.System;
        _tenants=new(StringComparer.Ordinal);
        foreach(var (id,c) in tenants)
        {
            if(!ValidId(id)||c is null||c.Threads<=0||c.Threads>capacity.Threads||c.MemoryBytes<=0||
                c.MemoryBytes>capacity.MemoryBytes||c.MaxRunning<=0||c.MaxRunning>capacity.MaxRunning||
                c.MaxQueued<=0||c.MaxQueued>capacity.MaxTracked)throw new ArgumentException("Invalid tenant capacity.",nameof(tenants));
            _tenants.Add(id,new(c));
            _rotation.AddLast(id);
        }
    }

    public Admission Submit(WorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if(request.Key is not { } k||!ValidId(k.TenantId)||!ValidId(k.ModelId)||!ValidId(k.RevisionId)||!ValidId(k.RunId)||
            request.Budget is not { } b||b.Threads<=0||b.MemoryBytes<=0||b.Deadline<=TimeSpan.Zero||
            b.Deadline>TimeSpan.FromDays(7)||request.FrozenInputSha256 is not {Length:64} digest||
            !digest.All(c=>char.IsAsciiHexDigitLower(c)))return Admission.Invalid;
        lock(_gate)
        {
            if(!_tenants.TryGetValue(k.TenantId,out var tenant))return Admission.UnknownTenant;
            if(_entries.TryGetValue(k,out var previous))return previous.Request==request?Admission.Duplicate:Admission.Conflict;
            if(b.Threads>tenant.Capacity.Threads||b.MemoryBytes>tenant.Capacity.MemoryBytes)return Admission.Invalid;
            SweepLocked();
            if(_entries.Count>=_capacity.MaxTracked||tenant.Queue.Count>=tenant.Capacity.MaxQueued)return Admission.Full;
            var entry=new Entry(request,_clock.GetTimestamp());
            _entries.Add(k,entry);
            tenant.Queue.AddLast(entry);
            return Admission.Accepted;
        }
    }

    // FIFO per tenant. A tenant at its own concurrency cap is skipped; once the next eligible
    // head fits its tenant but not the global pool, reserve its turn instead of starving large jobs.
    public WorkerLease? TryAcquire()
    {
        lock(_gate)
        {
            SweepLocked();
            if(_running>=_capacity.MaxRunning)return null;
            for(var node=_rotation.First;node is not null;node=node.Next)
            {
                var t=_tenants[node.Value];
                if(t.Queue.First is not { } head||t.Running>=t.Capacity.MaxRunning)continue;
                var e=head.Value; var b=e.Request.Budget;
                if(b.Threads>t.Capacity.Threads-t.Threads||b.MemoryBytes>t.Capacity.MemoryBytes-t.Bytes)continue;
                if(b.Threads>_capacity.Threads-_threads||b.MemoryBytes>_capacity.MemoryBytes-_bytes)return null;
                var remaining=Remaining(e);
                if(remaining<=TimeSpan.Zero) { e.State=RunState.TimedOut;t.Queue.RemoveFirst();return null; }
                var fence=checked(_fence+1);
                _fence=fence;
                t.Queue.RemoveFirst();
                _rotation.Remove(node);_rotation.AddLast(node);
                e.Fence=fence;e.ExecutionStarted=false;e.State=RunState.Running;
                t.Threads+=b.Threads;t.Bytes+=b.MemoryBytes;t.Running++;
                _threads+=b.Threads;_bytes+=b.MemoryBytes;_running++;
                return new(e.Request,_epoch,fence,remaining);
            }
            return null;
        }
    }

    public bool Cancel(WorkerRunKey key)
    {
        lock(_gate)
        {
            SweepLocked();
            if(!_entries.TryGetValue(key,out var e))return false;
            if(e.State==RunState.Queued)
            {
                _tenants[key.TenantId].Queue.Remove(e);e.State=RunState.Cancelled;return true;
            }
            if(e.State!=RunState.Running)return false;
            if(!e.ExecutionStarted){Release(e);e.State=RunState.Cancelled;return true;}
            e.State=RunState.Revoked;e.Revocation=RunState.Cancelled;return true;
        }
    }

    internal WorkerLease? TryBeginExecution(WorkerLease lease)
    {
        lock(_gate)
        {
            SweepLocked();
            if(!Matches(lease,out var e)||e!.State!=RunState.Running||e.ExecutionStarted)return null;
            var remaining=Remaining(e);
            if(remaining<=TimeSpan.Zero)return null;
            e.ExecutionStarted=true;return lease with {Remaining=remaining};
        }
    }

    public bool IsCurrent(WorkerLease lease)
    {
        lock(_gate)
        {
            SweepLocked();
            return Matches(lease,out var e)&&e!.State==RunState.Running;
        }
    }

    // The process boundary MUST attest that the entire owned execution scope has exited.
    // A lost/cancelled lease does not release reservation until cleanup; unknown cleanup quarantines.
    // CandidateEligible is not permission to publish a verified result.
    public WorkerCompletion Finish(WorkerLease lease,Termination termination,bool cleanupConfirmed)
    {
        if(!Enum.IsDefined(termination))throw new ArgumentOutOfRangeException(nameof(termination));
        lock(_gate)
        {
            SweepLocked();
            if(!Matches(lease,out var e)||e!.State is not (RunState.Running or RunState.Revoked or RunState.Quarantined))
                return new(false,false,e?.State??RunState.Failed);
            if(!cleanupConfirmed)
            {
                if(e.State==RunState.Running)e.Revocation=RunState.Failed;
                e.State=RunState.Quarantined;
                return new(true,false,e.State);
            }
            var candidate=e.ExecutionStarted&&e.State==RunState.Running&&termination==Termination.Completed;
            var final=e.State is RunState.Revoked or RunState.Quarantined?e.Revocation:termination switch
            {
                Termination.Completed=>RunState.Completed,Termination.Timeout=>RunState.TimedOut,
                Termination.Cancelled=>RunState.Cancelled,_=>RunState.Failed
            };
            Release(e);e.State=final;
            return new(true,candidate,final);
        }
    }

    // Retries are explicit host decisions; same frozen identity, same original deadline.
    // The prior process must be confirmed exited and its fence is never reusable.
    public bool RetryFailed(WorkerRunKey key)
    {
        lock(_gate)
        {
            SweepLocked();
            if(!_entries.TryGetValue(key,out var e)||e.State!=RunState.Failed||Remaining(e)<=TimeSpan.Zero)return false;
            var t=_tenants[key.TenantId];
            if(t.Queue.Count>=t.Capacity.MaxQueued)return false;
            e.State=RunState.Queued;t.Queue.AddLast(e);return true;
        }
    }

    public RunState? State(WorkerRunKey key)
    {
        lock(_gate){SweepLocked();return _entries.TryGetValue(key,out var e)?e.State:null;}
    }

    // Only after durable terminal outcome has been committed by the host (or local history).
    // Re-admission after forgetting requires durable deduplication outside this governor.
    public bool AcknowledgeTerminal(WorkerRunKey key)
    {
        lock(_gate)
        {
            if(!_entries.TryGetValue(key,out var e)||e.State is RunState.Queued or RunState.Running or RunState.Revoked or RunState.Quarantined)return false;
            return _entries.Remove(key);
        }
    }

    public GovernorSnapshot Snapshot()
    {
        lock(_gate){SweepLocked();return new(_running,_threads,_bytes,_tenants.Values.Sum(t=>t.Queue.Count),_entries.Count);}
    }

    private void SweepLocked()
    {
        foreach(var e in _entries.Values)
        {
            if(e.State is not (RunState.Queued or RunState.Running)||Remaining(e)>TimeSpan.Zero)continue;
            if(e.State==RunState.Queued){_tenants[e.Request.Key.TenantId].Queue.Remove(e);e.State=RunState.TimedOut;}
            else if(!e.ExecutionStarted){Release(e);e.State=RunState.TimedOut;}
            else{e.State=RunState.Revoked;e.Revocation=RunState.TimedOut;}
        }
    }
    private TimeSpan Remaining(Entry e)=>e.Request.Budget.Deadline-_clock.GetElapsedTime(e.Started,_clock.GetTimestamp());
    private bool Matches(WorkerLease lease,out Entry? e)
    {
        e=null;
        return lease is not null&&lease.Epoch==_epoch&&_entries.TryGetValue(lease.Request.Key,out e)&&
            e.Request==lease.Request&&e.Fence==lease.Fence;
    }
    private void Release(Entry e)
    {
        var b=e.Request.Budget;var t=_tenants[e.Request.Key.TenantId];
        t.Threads-=b.Threads;t.Bytes-=b.MemoryBytes;t.Running--;
        _threads-=b.Threads;_bytes-=b.MemoryBytes;_running--;
    }
    private static bool ValidId(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=256&&value==value.Trim()&&!value.Any(char.IsControl);
}
