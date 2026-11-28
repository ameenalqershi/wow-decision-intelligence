using ComponentStudy;
using System.Numerics;
using System.Diagnostics;
namespace AlgorithmDiscoveryLab;

// Bounded research policy: a mathematical gate is never an optimality assertion
// until a feasible witness exactly closes a valid transported lower bound.
internal sealed class AdaptiveDecision(bool adaptive) : ICandidate
{
    private Model? previous;
    private Optimum? optimum;
    private readonly FullSearch warm=new(true);
    private readonly OrientationSearch orientation=new(false);
    private readonly CachedQuotaResidual quotaCache=new();
    private int trials, successes;
    internal string Route {get;private set;}="";
    internal string Reason {get;private set;}="";
    internal string Certificate {get;private set;}="";
    internal int Attempts {get;private set;}
    internal int WorkEstimate {get;private set;}
    internal long? Bound {get;private set;}
    internal bool CacheHit {get;private set;}
    internal bool Solved {get;private set;}
    public Answer Solve(Model model)
    {
        long start=Stopwatch.GetTimestamp();
        Route="";Reason="";Certificate="";Attempts=0;Bound=null;CacheHit=false;Solved=false;
        int n=model.Size;
        int[] c=Corpus.Prices(model);
        var edges=model.Capacities.Where(r=>r.Lower==1&&r.Upper==1&&BitOperations.PopCount((uint)r.Scope)==2)
            .Select(r=>(BitOperations.TrailingZeroCount((uint)r.Scope),BitOperations.Log2((uint)r.Scope))).ToArray();
        // This proxy is a heuristic leaf count, not a prediction of measured time.
        // Eligible pair count is only a heuristic; dependent edges can overstate compression.
        int rough=Math.Max(1,n-Math.Min(edges.Length,n-1));
        WorkEstimate=1<<rough;
        Optimum? hit=null;
        if(previous is {} old&&optimum is {} witness&&old.Size==n&&old.Capacities.All(model.Capacities.Contains)
            && Oracle.Evaluate(model,witness.Mask) is int feasible)
        {
            var prior=Corpus.Prices(old);
            var delta=c.Select((v,i)=>(long)v-prior[i]).ToArray();
            bool Try(string name,long? residual)
            {
                Attempts++;
                if(residual is not long d)return false;
                long b=checked(witness.Value+d);
                Bound=Bound.HasValue?Math.Max(Bound.Value,b):b;
                if(b!=feasible)return false;
                Certificate=name;hit=new(witness.Mask,feasible);return true;
            }
            if(delta.All(x=>x==0))
            {
                Try("identity/restriction",0);Reason="unchanged objective, row containment and feasible witness";
            }
            else
            {
                // Prospective conservative operation-count gate. Prior success rate has
                // a beta(1,1) smoothing prior; do not treat it as a calibrated probability.
                int changed=delta.Count(x=>x!=0);
                bool allowBox=!adaptive || n>=8 && (changed<=n/2 || (successes+1)*2>=trials+2);
                if(allowBox)
                {
                    trials++;
                    if(Try("box",delta.Sum(x=>Math.Min(0,x))))successes++;
                    else
                    {
                        // One stronger level at most in the adaptive arm. Unconditional
                        // hierarchy tries parity and optional quota. No unbounded cascade.
                        var quota=model.Capacities.FirstOrDefault(r=>r.Lower==r.Upper&&BitOperations.PopCount((uint)r.Scope)>2);
                        bool allowStrong=!adaptive || WorkEstimate>=4*n && edges.Length>0;
                        if(allowStrong)
                        {
                            if(adaptive&&quota!=null)
                            {
                                var members=Enumerable.Range(0,n).Select(i=>(quota.Scope&(1<<i))!=0).ToArray();
                                int h=quotaCache.Hits;
                                Try("normalized-quota",quotaCache.Minimum(delta,edges,new(members,quota.Lower)));
                                CacheHit=quotaCache.Hits>h;
                            }
                            else
                            {
                                Try("parity",ParityFloor.Minimum(delta,model.Capacities));
                                if(!adaptive&&hit==null&&quota!=null)
                                {
                                    var members=Enumerable.Range(0,n).Select(i=>(quota.Scope&(1<<i))!=0).ToArray();
                                    int h=quotaCache.Hits;
                                    Try("normalized-quota",quotaCache.Minimum(delta,edges,new(members,quota.Lower)));
                                    CacheHit=quotaCache.Hits>h;
                                }
                            }
                        }
                    }
                    Reason="admitted certificate: bounded attempts, structural proxy and smoothed box history";
                }
                else Reason="certificate skipped: small model or high change / low observed box success";
            }
        }
        else Reason="no surviving feasible prior witness with compatible restricted domain";
        Answer result;
        long gate=Stopwatch.GetTimestamp()-start;
        if(hit is {} accepted){Route="transport";result=new(accepted,0,0,0,true,gate);}
        else
        {
            Solved=true;
            bool compressed=n>=8&&edges.Length>=n/3;
            Route=compressed?"orientation":"warm";
            result=(compressed?orientation.Solve(model):warm.Solve(model)) with {GateTicks=gate};
        }
        previous=new(model.Size,model.Capacities.ToArray(),model.Costs.ToArray());
        optimum=result.Best;
        return result;
    }
}
