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
