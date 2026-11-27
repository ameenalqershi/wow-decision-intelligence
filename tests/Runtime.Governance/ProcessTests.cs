using System.Diagnostics;
using System.Text;
using WowDi.Solver.Contracts;
using Xunit;

namespace WowDi.Runtime.Governance.Tests;

public sealed class ProcessTests
{
    private static string Dotnet=>Environment.GetEnvironmentVariable("WOWDI_TEST_DOTNET")??
        throw new InvalidOperationException("WOWDI_TEST_DOTNET is required.");
    private static string Bootstrap=>Environment.GetEnvironmentVariable("WOWDI_TEST_BOOTSTRAP")??
        throw new InvalidOperationException("WOWDI_TEST_BOOTSTRAP is required.");
    private static string Fixture=>Environment.GetEnvironmentVariable("WOWDI_TEST_FIXTURE")??
        throw new InvalidOperationException("WOWDI_TEST_FIXTURE is required.");
    private static WorkerProgram Program(string mode)=>new(Dotnet,[Fixture,mode],AppContext.BaseDirectory);
    private static WorkerProcessBoundary Boundary(IWorkerProcessScopeFactory factory,int max=1024)=>
        new(factory,new(Dotnet,[Bootstrap],AppContext.BaseDirectory),max);

    [Theory]
    [InlineData("success",Termination.Completed,"candidate")]
    [InlineData("crash",Termination.Failed,null)]
    [InlineData("overflow",Termination.Failed,null)]
    [InlineData("empty",Termination.Failed,null)]
    public async Task RealChildProcessOutcomeAndOutputBounds(string mode,Termination expected,string? value)
    {
        var governor=GovernorTests.Governor(1,1);
        var request=GovernorTests.Request("A","run");
        governor.Submit(request);
        var scope=new FixtureScopeFactory();
        var executor=new WorkerExecutor(governor,Boundary(scope));
        var (completion,result)=await executor.RunAsync(governor.TryAcquire()!,Program(mode));
        Assert.Equal(expected,result.Termination);
        Assert.True(result.CleanupConfirmed);
        Assert.Equal(result.Resources.ElapsedMs!.Value,result.ReservedCoreMilliseconds);
        Assert.Equal(expected==Termination.Completed,completion.CandidateEligible);
        Assert.Equal(value,result.Candidate is null?null:Encoding.UTF8.GetString(result.Candidate));
        Assert.Equal(0,governor.Snapshot().Running);
        Assert.Null(result.Resources.CpuMs);
        Assert.Null(result.Resources.PeakBytes);
        Assert.True(scope.Exited);
    }

    [Fact]
    public async Task RealCancellationStopsProcessAndReleasesOnlyAfterExit()
    {
        var governor=GovernorTests.Governor(1,1);
        var a=GovernorTests.Request("A","a");var b=GovernorTests.Request("B","b");
        governor.Submit(a);governor.Submit(b);
        var scope=new FixtureScopeFactory();
        var executor=new WorkerExecutor(governor,Boundary(scope));
        var lease=governor.TryAcquire()!;
        var execution=executor.RunAsync(lease,Program("slow"));
        await scope.Attached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(governor.TryAcquire());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>executor.RunAsync(lease,Program("slow")));
        Assert.True(governor.Cancel(a.Key));
        var (completion,result)=await execution;
        Assert.Equal(RunState.Cancelled,completion.State);
        Assert.True(result.CleanupConfirmed);
        Assert.Equal(result.Resources.ElapsedMs!.Value,result.ReservedCoreMilliseconds);
        Assert.Null(result.Candidate);
        Assert.True(scope.Exited);
        Assert.Equal(b.Key,governor.TryAcquire()!.Request.Key);
    }

    [Fact]
    public async Task RealDeadlineExpiresAndDoesNotPublish()
    {
        var governor=GovernorTests.Governor(1,1);
        governor.Submit(GovernorTests.Request("A","run",deadline:TimeSpan.FromSeconds(2)));
        var scope=new FixtureScopeFactory();
        var execution=await new WorkerExecutor(governor,Boundary(scope)).RunAsync(governor.TryAcquire()!,Program("slow"));
        Assert.Equal(RunState.TimedOut,execution.Completion.State);
        Assert.True(execution.Result.CleanupConfirmed);
        Assert.Null(execution.Result.Candidate);
        Assert.True(scope.Exited);
        Assert.Equal(0,governor.Snapshot().Running);
    }

    [Fact]
    public async Task MissingContainmentFailsClosedBeforeAdapterStarts()
    {
        var governor=GovernorTests.Governor(1,1);
        governor.Submit(GovernorTests.Request("A","run"));
        var executor=new WorkerExecutor(governor,Boundary(new LinuxCgroupScopeFactory("/not-a-delegated-cgroup")));
        var result=await executor.RunAsync(governor.TryAcquire()!,Program("success"));
        Assert.Equal(Termination.Failed,result.Result.Termination);
        Assert.Null(result.Result.Candidate);
        Assert.True(result.Result.CleanupConfirmed);
        Assert.Equal(0,governor.Snapshot().Running);
    }

    // Test-only containment: all fixture processes are trusted and never create descendants.
    // The bootstrap creates one fixture process. This is NOT a production OS resource boundary.
    private sealed class FixtureScopeFactory:IWorkerProcessScopeFactory
    {
        internal TaskCompletionSource Attached{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exited;
        public ValueTask<IWorkerProcessScope> CreateAsync(WorkerBudget b,CancellationToken ct)=>
            ValueTask.FromResult<IWorkerProcessScope>(new Scope(this));
        private sealed class Scope(FixtureScopeFactory owner):IWorkerProcessScope
        {
            private Process? _process;
            public ValueTask AttachAsync(Process p,CancellationToken ct){_process=p;owner.Attached.TrySetResult();return ValueTask.CompletedTask;}
            public async ValueTask<ScopeCompletion> CompleteAsync(CancellationToken ct)
            {
                if(_process is not null)
                {
                    if(!_process.HasExited)_process.Kill(entireProcessTree:true);
                    await _process.WaitForExitAsync(ct);
                    owner.Exited=_process.HasExited;
                }
                return new(true,false,null,null,null);
            }
            public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        }
    }
}
