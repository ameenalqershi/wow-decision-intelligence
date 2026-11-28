using System.Collections.Concurrent;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;

namespace WowDi.Solver.Portfolio;

/// <summary>Executes portfolio members through the S07 governor and OS process boundary of one portfolio run.
/// The pool's governor capacity equals the portfolio thread and memory caps, so the kernel quota (cgroup cpu.max /
/// job object), the governor reservation and the portfolio ledger enforce the same limits independently.</summary>
public sealed class GovernedPool
{
    private readonly WorkerGovernor _governor;
    private readonly WorkerExecutor _executor;
    private readonly ConcurrentDictionary<WorkerRunKey,(WorkerProgram Program,TaskCompletionSource<(WorkerCompletion,BoundaryResult)> Done,CancellationToken Ct)> _pending=new();
    private readonly object _pump=new();
    public string TenantId {get;}

    public GovernedPool(string tenantId,int threads,long memoryBytes,int maxRunning,WorkerProcessBoundary boundary,TimeProvider? clock=null)
    {
        TenantId=tenantId;
        _governor=new WorkerGovernor(new WorkerCapacity(threads,memoryBytes,maxRunning,Math.Max(maxRunning,64)),
            new Dictionary<string,TenantCapacity>{[tenantId]=new(threads,memoryBytes,maxRunning,64)},clock);
        _executor=new WorkerExecutor(_governor,boundary);
    }

    public Task<(WorkerCompletion Completion,BoundaryResult Result)> RunAsync(WorkerRequest request,WorkerProgram program,CancellationToken ct)
    {
        var done=new TaskCompletionSource<(WorkerCompletion,BoundaryResult)>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register before submission so a concurrent pump can always find the owner of a granted lease.
        if(!_pending.TryAdd(request.Key,(program,done,ct)))return Task.FromResult(Rejected("portfolio.duplicateRun"));
        var admission=_governor.Submit(request);
        if(admission!=Admission.Accepted){_pending.TryRemove(request.Key,out _);return Task.FromResult(Rejected("portfolio.admission."+admission));}
        // Cancellation before dispatch: withdraw the queued request; after dispatch the S07 executor owns cancellation.
        ct.Register(()=>
        {
            if(_pending.TryRemove(request.Key,out var e)&&_governor.Cancel(request.Key))
                e.Done.TrySetResult((new WorkerCompletion(true,false,RunState.Cancelled),
                    new BoundaryResult(Termination.Cancelled,null,null,new(0,0,null),true,"portfolio.cancelledBeforeStart",null,null,0)));
        });
        Pump();
        return done.Task;
    }
    private static (WorkerCompletion,BoundaryResult) Rejected(string code)=>(new WorkerCompletion(false,false,RunState.Failed),
        new BoundaryResult(Termination.Failed,null,null,new(0,0,null),true,code,null,null,0));

    // Dispatch every lease the governor grants to the member that submitted it.
    private void Pump()
    {
        lock(_pump)
        {
            while(_governor.TryAcquire() is {} lease)
            {
                if(!_pending.TryRemove(lease.Request.Key,out var entry))
                {_governor.Finish(lease,Termination.Failed,cleanupConfirmed:true);continue;}
                _=Task.Run(async()=>
                {
                    try
                    {
                        var outcome=await _executor.RunAsync(lease,entry.Program,entry.Ct).ConfigureAwait(false);
                        entry.Done.TrySetResult(outcome);
                    }
                    catch(Exception ex){entry.Done.TrySetException(ex);}
                    finally{_governor.AcknowledgeTerminal(lease.Request.Key);Pump();}
                });
            }
        }
    }
}

/// <summary>A portfolio member backed by a trusted worker registration. <paramref name="program"/> maps an assignment to the
/// registered worker command line; <paramref name="decode"/> maps the bounded worker output to a solver result (adapter-owned,
/// S02/S03). CPU is the boundary measurement; the member never reports its own resource use.</summary>
public sealed class GovernedMember(AdapterProfile profile,GovernedPool pool,Func<MemberAssignment,WorkerProgram> program,
    Func<byte[],SolverResult?> decode,string frozenInputSha256):IPortfolioMember
{
    public AdapterProfile Profile=>profile;

    public async Task<MemberExecution> ExecuteAsync(MemberAssignment a,CancellationToken ct)
    {
        var model=a.Plan.Lowered.Original.Original.Model;
        var key=new WorkerRunKey(pool.TenantId,model.ModelId,model.RevisionId,a.RunId);
        var (completion,result)=await pool.RunAsync(new WorkerRequest(key,new WorkerBudget(a.Threads,a.MemoryBytes,a.HardDeadline),
            frozenInputSha256),program(a),ct).ConfigureAwait(false);
        SolverResult? decoded=null;string? diagnostic=result.Diagnostic;
        if(result.Candidate is {} bytes&&completion.CandidateEligible)
        {
            try{decoded=decode(bytes);}
            catch(Exception ex) when(ex is not OutOfMemoryException){diagnostic="portfolio.decode:"+ex.GetType().Name;}
            if(decoded is null)diagnostic??="portfolio.decode";
        }
        return new(result.Termination,decoded,result.Resources.ElapsedMs??0,result.Resources.CpuMs,result.ScopePeakBytes,
            result.CleanupConfirmed,diagnostic,result.Resources.CpuMs is null?CpuSource.ReservationBound:CpuSource.Measured);
    }
}
