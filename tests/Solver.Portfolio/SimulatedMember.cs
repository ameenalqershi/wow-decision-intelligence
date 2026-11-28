using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;

namespace WowDi.Solver.Portfolio.Tests;

/// <summary>Simulated (not native) member behaviours. Every outcome is labelled CpuSource.Simulated so simulated
/// evidence can never be confused with boundary-measured engine runs.</summary>
public enum Behaviour
{
    HonestOptimal, HonestFeasible, LiarOptimal, CorruptInfeasible, WrongObjective, ForeignIdentity, Throws, FailedExit,
    HangCooperative, HangIgnoringCancel, CleanupUnconfirmed, Overspend, ClaimInfeasible, HonestInfeasible, NoResult
}

/// <summary>Tracks concurrently running simulated threads and live executions across a run.</summary>
public sealed class Probe
{
    private int _threads,_live,_peak;
    public int Peak=>Volatile.Read(ref _peak);
    public int Live=>Volatile.Read(ref _live);
    public int Started;
    public void Enter(int threads)
    {
        Interlocked.Increment(ref _live);Interlocked.Increment(ref Started);
        var now=Interlocked.Add(ref _threads,threads);
        int p;while(now>(p=Volatile.Read(ref _peak))&&Interlocked.CompareExchange(ref _peak,now,p)!=p){}
    }
    public void Exit(int threads){Interlocked.Add(ref _threads,-threads);Interlocked.Decrement(ref _live);}
}

public sealed class SimulatedMember(AdapterProfile profile,Ilp ilp,Behaviour behaviour,int delayMs,Probe probe):IPortfolioMember
{
    public AdapterProfile Profile=>profile;
    public Behaviour Behaviour=>behaviour;
    public bool SawCancellation {get;private set;}

    public async Task<MemberExecution> ExecuteAsync(MemberAssignment a,CancellationToken ct)
    {
        probe.Enter(a.Threads);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if(behaviour==Behaviour.Throws)throw new InvalidOperationException("simulated crash");
            if(behaviour==Behaviour.HangIgnoringCancel)
            {
                await Task.Delay(a.HardDeadline+TimeSpan.FromMilliseconds(400),CancellationToken.None);
                return Done(Termination.Completed,null,clock);
            }
            if(behaviour==Behaviour.HangCooperative)
            {
                try{await Task.Delay(Timeout.Infinite,ct);}
                catch(OperationCanceledException){SawCancellation=true;}
                return Done(Termination.Timeout,null,clock);
            }
            if(delayMs>0)
            {
                try{await Task.Delay(delayMs,ct);}
                catch(OperationCanceledException){SawCancellation=true;return Done(Termination.Cancelled,null,clock);}
            }
            var (opt,best,worse)=ilp.Oracle();
            var id=profile.Manifest.Solver;
            SolverResult? r=behaviour switch
            {
                Behaviour.HonestOptimal when best is not null=>Fixtures.Result(ilp,a,id,best,MathematicalStatus.Optimal,bound:opt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Behaviour.HonestFeasible when best is not null=>Fixtures.Result(ilp,a,id,worse??best,MathematicalStatus.Feasible,bound:opt!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Behaviour.LiarOptimal when best is not null=>Fixtures.Result(ilp,a,id,worse??best,MathematicalStatus.Optimal,
                    bound:ilp.Value(worse??best).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Behaviour.CorruptInfeasible=>Fixtures.Result(ilp,a,id,ilp.Infeasible(),MathematicalStatus.Optimal),
                Behaviour.WrongObjective when best is not null=>Fixtures.Result(ilp,a,id,best,MathematicalStatus.Optimal,reportedValue:(ilp.Value(best)+1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Behaviour.ForeignIdentity when best is not null=>Fixtures.Result(ilp,a,new("impostor","9","test-rid"),best,MathematicalStatus.Optimal),
                Behaviour.CleanupUnconfirmed when best is not null=>Fixtures.Result(ilp,a,id,best,MathematicalStatus.Optimal),
                Behaviour.Overspend when best is not null=>Fixtures.Result(ilp,a,id,worse??best,MathematicalStatus.Feasible),
                Behaviour.ClaimInfeasible=>Fixtures.Result(ilp,a,id,null,MathematicalStatus.Infeasible),
                Behaviour.HonestInfeasible when best is null=>Fixtures.Result(ilp,a,id,null,MathematicalStatus.Infeasible),
                _=>null
            };
            if(behaviour==Behaviour.FailedExit)return Done(Termination.Failed,null,clock);
            var exec=Done(Termination.Completed,r,clock);
            if(behaviour==Behaviour.CleanupUnconfirmed)exec=exec with{CleanupConfirmed=false};
            if(behaviour==Behaviour.Overspend)exec=exec with{CpuMs=(long)a.Threads*(long)a.HardDeadline.TotalMilliseconds+137};
            return exec;
        }
        finally{probe.Exit(a.Threads);}
        MemberExecution Done(Termination t,SolverResult? r,System.Diagnostics.Stopwatch c)=>
            new(t,r,c.ElapsedMilliseconds,(long)a.Threads*c.ElapsedMilliseconds,null,true,null,CpuSource.Simulated);
    }
}
