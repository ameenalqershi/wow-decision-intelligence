using System.Collections.Immutable;
using WowDi.Solver.Contracts;
using Xunit;

namespace WowDi.Runtime.Governance.Tests;

public sealed class GovernorTests
{
    internal static WorkerRequest Request(string tenant,string run,int threads=1,long bytes=64*1024*1024,TimeSpan? deadline=null)=>
        new(new(tenant,"model","revision",run),new(threads,bytes,deadline??TimeSpan.FromSeconds(30)),new string('a',64));
    internal static WorkerGovernor Governor(int cores=2,int maxRunning=2,TimeProvider? clock=null)=>
        new(new(cores,256*1024*1024,maxRunning,128),new Dictionary<string,TenantCapacity>
        {["A"]=new(cores,256*1024*1024,maxRunning,32),["B"]=new(cores,256*1024*1024,maxRunning,32)},clock);

    [Fact]
    public void FairTenantRotationAndNoOversubscription()
    {
        var governor=Governor();
        foreach(var id in Enumerable.Range(0,4))Assert.Equal(Admission.Accepted,governor.Submit(Request("A",id.ToString())));
        Assert.Equal(Admission.Accepted,governor.Submit(Request("B","0")));
        var a=Assert.IsType<WorkerLease>(governor.TryAcquire());
        var b=Assert.IsType<WorkerLease>(governor.TryAcquire());
        Assert.NotNull(governor.TryBeginExecution(a));
        Assert.NotNull(governor.TryBeginExecution(b));
        Assert.Equal("A",a.Request.Key.TenantId);
        Assert.Equal("B",b.Request.Key.TenantId);
        Assert.Null(governor.TryAcquire());
        Assert.Equal(2,governor.Snapshot().ReservedThreads);
        Assert.True(governor.Finish(a,Termination.Completed,true).CandidateEligible);
        Assert.False(governor.Finish(a,Termination.Completed,true).Accepted);
        var next=Assert.IsType<WorkerLease>(governor.TryAcquire());
        Assert.Equal("1",next.Request.Key.RunId);
        governor.Finish(b,Termination.Failed,true);
        governor.Finish(next,Termination.Completed,true);
        while(governor.TryAcquire() is { } lease)governor.Finish(lease,Termination.Completed,true);
        Assert.Equal(new GovernorSnapshot(0,0,0,0,5),governor.Snapshot());
    }

    [Fact]
    public void LargeHeadReservesTurnAndPerTenantResourceLimitsApply()
    {
        var governor=Governor(cores:2);
        var first=Request("A","first");
        var large=Request("B","large",threads:2);
        var small=Request("A","small");
        foreach(var job in new[]{first,large,small})Assert.Equal(Admission.Accepted,governor.Submit(job));
        var running=governor.TryAcquire()!;
        Assert.Null(governor.TryAcquire());
        governor.Finish(running,Termination.Completed,true);
        var next=governor.TryAcquire()!;
        Assert.Equal(large.Key,next.Request.Key);
        Assert.Null(governor.TryAcquire());
        governor.Finish(next,Termination.Completed,true);
        Assert.Equal(small.Key,governor.TryAcquire()!.Request.Key);
        Assert.Equal(Admission.Invalid,governor.Submit(Request("A","oversize",threads:3)));
        Assert.Equal(Admission.UnknownTenant,governor.Submit(Request("foreign","r")));
    }

    [Fact]
    public void CancellationTimeoutAndUnconfirmedCleanupDoNotReleaseCapacity()
    {
        var clock=new FakeClock();
        var governor=Governor(cores:1,maxRunning:1,clock:clock);
        var a=Request("A","a",deadline:TimeSpan.FromSeconds(2));
        var b=Request("B","b");
        governor.Submit(a);governor.Submit(b);
        var lease=governor.TryAcquire()!;
        Assert.NotNull(governor.TryBeginExecution(lease));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(governor.IsCurrent(lease));
        Assert.Equal(RunState.Revoked,governor.State(a.Key));
        Assert.Null(governor.TryAcquire());
        Assert.Equal(RunState.Quarantined,governor.Finish(lease,Termination.Failed,false).State);
        Assert.Equal(1,governor.Snapshot().Running);
        Assert.Null(governor.TryAcquire());
        var complete=governor.Finish(lease,Termination.Completed,true);
        Assert.Equal(RunState.TimedOut,complete.State);
        Assert.False(complete.CandidateEligible);
        var second=governor.TryAcquire()!;
        Assert.NotNull(governor.TryBeginExecution(second));
        Assert.True(governor.Cancel(b.Key));
        Assert.False(governor.IsCurrent(second));
        Assert.Equal(RunState.Cancelled,governor.Finish(second,Termination.Completed,true).State);
        Assert.Equal(0,governor.Snapshot().ReservedThreads);
    }

    [Fact]
    public void RetryRetainsFrozenInputAndRejectsStaleAndForeignFences()
    {
        var governor=Governor();
        var job=Request("A","run");
        Assert.Equal(Admission.Accepted,governor.Submit(job));
        Assert.Equal(Admission.Duplicate,governor.Submit(job));
        Assert.Equal(Admission.Conflict,governor.Submit(job with{FrozenInputSha256=new string('b',64)}));
        var old=governor.TryAcquire()!;
        governor.Finish(old,Termination.Failed,true);
        Assert.True(governor.RetryFailed(job.Key));
        var retry=governor.TryAcquire()!;
        Assert.NotNull(governor.TryBeginExecution(retry));
        Assert.True(retry.Fence>old.Fence);
        Assert.Equal(old.Request,retry.Request);
        Assert.False(governor.Finish(old,Termination.Completed,true).Accepted);
        Assert.False(governor.Finish(retry with{Epoch=Guid.NewGuid()},Termination.Completed,true).Accepted);
        Assert.True(governor.Finish(retry,Termination.Completed,true).CandidateEligible);
        Assert.False(governor.RetryFailed(job.Key));
        Assert.True(governor.AcknowledgeTerminal(job.Key));
        Assert.Equal(0,governor.Snapshot().Tracked);
    }

    [Fact]
    public async Task ConcurrentAdmissionsAndCompletionsNeverExceedConfiguredBounds()
    {
        var governor=Governor();
        var admissions=await Task.WhenAll(Enumerable.Range(0,100).Select(i=>Task.Run(()=>governor.Submit(Request(i%2==0?"A":"B",i.ToString())))));
        Assert.Equal(64,admissions.Count(a=>a==Admission.Accepted));
        Assert.Equal(36,admissions.Count(a=>a==Admission.Full));
        var leases=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Task.Run(governor.TryAcquire)));
        var active=leases.OfType<WorkerLease>().ToArray();
        Assert.Equal(2,active.Length);
        Assert.Equal(2,governor.Snapshot().Running);
        var completions=await Task.WhenAll(Enumerable.Range(0,40).Select(i=>Task.Run(()=>governor.Finish(active[i%2],Termination.Completed,true))));
        Assert.Equal(2,completions.Count(x=>x.Accepted));
        Assert.Equal(0,governor.Snapshot().Running);
        Assert.Equal(62,governor.Snapshot().Queued);
    }

    [Fact]
    public void QueuedDeadlineAndCancellationNeverAcquireResource()
    {
        var clock=new FakeClock();
        var governor=Governor(clock:clock);
        var a=Request("A","a",deadline:TimeSpan.FromSeconds(1));var b=Request("B","b");
        governor.Submit(a);governor.Submit(b);
        Assert.True(governor.Cancel(b.Key));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(governor.TryAcquire());
        Assert.Equal(RunState.TimedOut,governor.State(a.Key));
        Assert.Equal(RunState.Cancelled,governor.State(b.Key));
        Assert.Equal(0,governor.Snapshot().ReservedBytes);
    }

    [Fact]
    public void CancelOrExpireAcquiredButNotStartedLeaseReclaimsReservation()
    {
        var clock=new FakeClock();
        var governor=Governor(1,1,clock);
        var a=Request("A","a");var b=Request("B","b",deadline:TimeSpan.FromSeconds(1));
        governor.Submit(a);governor.Submit(b);
        var first=governor.TryAcquire()!;
        Assert.True(governor.Cancel(a.Key));
        Assert.Null(governor.TryBeginExecution(first));
        Assert.Equal(0,governor.Snapshot().Running);
        var second=governor.TryAcquire()!;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(governor.TryBeginExecution(second));
        Assert.Equal(RunState.TimedOut,governor.State(b.Key));
        Assert.Equal(0,governor.Snapshot().ReservedBytes);
    }

    private sealed class FakeClock:TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan value)=>Interlocked.Add(ref _ticks,value.Ticks);
    }
}
