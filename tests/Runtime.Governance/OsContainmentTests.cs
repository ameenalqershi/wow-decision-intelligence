using System.Diagnostics;
using System.Text;
using WowDi.Solver.Contracts;
using Xunit;

namespace WowDi.Runtime.Governance.Tests;

[Trait("Category","OSContainment")]
public sealed class OsContainmentTests
{
    private static string Env(string name)=>Environment.GetEnvironmentVariable(name)??throw new InvalidOperationException(name+" is required.");
    private static WorkerProcessBoundary Boundary()
    {
        IWorkerProcessScopeFactory scope=OperatingSystem.IsWindows()?new WindowsJobScopeFactory():
            new LinuxCgroupScopeFactory(Env("WOWDI_TEST_CGROUP_ROOT"));
        return new(scope,new(Env("WOWDI_TEST_DOTNET"),[Env("WOWDI_TEST_BOOTSTRAP")],AppContext.BaseDirectory));
    }
    private static WorkerProgram Program(string mode,string? marker=null)=>
        new(Env("WOWDI_TEST_DOTNET"),marker is null?[Env("WOWDI_TEST_FIXTURE"),mode]:[Env("WOWDI_TEST_FIXTURE"),mode,marker],AppContext.BaseDirectory);

    private static (WorkerGovernor Governor,WorkerLease Lease) Admit(string run,TimeSpan? timeout=null)
    {
        var governor=GovernorTests.Governor(1,1);
        Assert.Equal(Admission.Accepted,governor.Submit(GovernorTests.Request("A",run,bytes:256*1024*1024,deadline:timeout??TimeSpan.FromSeconds(20))));
        return (governor,governor.TryAcquire()!);
    }

    [Fact]
    public async Task RealOsBoundaryMeasuresCpuAndMemoryWithOneCoreCap()
    {
        var (governor,lease)=Admit("cpu");
        var (completion,result)=await new WorkerExecutor(governor,Boundary()).RunAsync(lease,Program("cpu"));
        Assert.Equal(Termination.Completed,result.Termination);
        Assert.True(completion.CandidateEligible);
        Assert.True(result.CleanupConfirmed);
        Assert.Equal("candidate",Encoding.UTF8.GetString(result.Candidate!));
        Assert.True(result.Resources.CpuMs>0);
        Assert.True(result.ScopePeakBytes>0);
        Assert.NotNull(result.ScopePeakMetric);
        // Allow scheduling/accounting granularity; do not report reserved core time as CPU.
        Assert.True(result.Resources.CpuMs<=result.Resources.ElapsedMs+500);
        Assert.Equal(0,governor.Snapshot().Running);
    }

    [Fact]
    public async Task RealOsBoundaryRejectsAllocationAboveAggregateMemoryCap()
    {
        var (governor,lease)=Admit("memory");
        var (completion,result)=await new WorkerExecutor(governor,Boundary()).RunAsync(lease,Program("memory"));
        Assert.False(completion.CandidateEligible);
        Assert.NotEqual(Termination.Completed,result.Termination);
        Assert.Null(result.Candidate);
        Assert.True(result.CleanupConfirmed);
        Assert.True(result.ScopePeakBytes>0);
        Assert.Equal(0,governor.Snapshot().Running);
    }

    [Fact]
    public async Task TwoRealTenantWorkersAndCrashRetryRemainFenced()
    {
        const long memory=256L*1024*1024;
        var governor=new WorkerGovernor(new(2,memory*2,2,16),new Dictionary<string,TenantCapacity>
        {["A"]=new(1,memory,1,4),["B"]=new(1,memory,1,4)});
        var a=GovernorTests.Request("A","run",bytes:memory);var b=GovernorTests.Request("B","run",bytes:memory);
        governor.Submit(a);governor.Submit(b);
        var first=governor.TryAcquire()!;var second=governor.TryAcquire()!;
        Assert.Equal("A",first.Request.Key.TenantId);Assert.Equal("B",second.Request.Key.TenantId);
        var executor=new WorkerExecutor(governor,Boundary());
        var results=await Task.WhenAll(executor.RunAsync(first,Program("crash")),executor.RunAsync(second,Program("cpu")));
        Assert.Equal(RunState.Failed,results[0].Completion.State);
        Assert.Equal(RunState.Completed,results[1].Completion.State);
        Assert.All(results,x=>Assert.True(x.Result.CleanupConfirmed));
        Assert.Equal(0,governor.Snapshot().Running);
        Assert.True(governor.RetryFailed(a.Key));
        var retry=governor.TryAcquire()!;
        Assert.True(retry.Fence>first.Fence);
        Assert.False(governor.Finish(first,Termination.Completed,true).Accepted);
        var recovered=await executor.RunAsync(retry,Program("success"));
        Assert.True(recovered.Completion.CandidateEligible,$"{recovered.Completion.State}; {recovered.Result.Diagnostic}; exit={recovered.Result.ExitCode}; cleanup={recovered.Result.CleanupConfirmed}");
        Assert.Equal(0,governor.Snapshot().ReservedBytes);
    }

    [Fact]
    public async Task RealOsBoundaryCancelsStartedAdapterAndConfirmsEmptyScope()
    {
        var (governor,lease)=Admit("cancel");
        var directory=Directory.CreateTempSubdirectory("wowdi-worker-");
        var marker=Path.Combine(directory.FullName,"started");
        try
        {
            var running=new WorkerExecutor(governor,Boundary()).RunAsync(lease,Program("slow",marker));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while(!File.Exists(marker)&&!running.IsCompleted)await Task.Delay(25,timeout.Token);
                Assert.True(File.Exists(marker),"Adapter never reached its actual execution.");
                Assert.True(governor.Cancel(lease.Request.Key));
            }
            finally { if(!running.IsCompleted)governor.Cancel(lease.Request.Key); }
            var (completion,result)=await running;
            Assert.Equal(RunState.Cancelled,completion.State);
            Assert.True(result.CleanupConfirmed);
            Assert.Null(result.Candidate);
            Assert.Equal(0,governor.Snapshot().ReservedBytes);
        }
        finally { directory.Delete(recursive:true); }
    }
}
