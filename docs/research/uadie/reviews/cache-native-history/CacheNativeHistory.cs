using AlgorithmDiscoveryLab;
using Google.OrTools.Sat;
using System.Text.Json;
using System.Diagnostics;

void Check(string name,bool ok)
{
    Console.WriteLine(JsonSerializer.Serialize(new{kind="boundary",name,valid=ok}));
    if(!ok)throw new Exception(name);
}
bool Reject<T>(Action a) where T:Exception{try{a();return false;}catch(T){return true;}}
if(args is ["boundary"])
{
    var cache=new CachedQuotaResidual();
    var e=new (int,int)[]{(0,1)};var q=new CardinalityResidual.Row([true,false],1);
    Check("initial",cache.Minimum([4,-2],e,q)==4);
    Check("fresh-cost",cache.Minimum([-9,8],e,q)==-9&&cache.Hits==1);
    q.Member[0]=false;q.Member[1]=true;
    Check("same-array-membership",cache.Minimum([4,-2],e,q)==-2&&cache.Builds==2);
    e[0]=(0,0);
    Check("same-array-edge",cache.Minimum([4,-2],e,q)==null&&cache.Builds==3);
    e[0]=(0,1);
    Check("restore-after-infeasible",cache.Minimum([4,-2],e,q)==-2&&cache.Builds==4);
    Check("rejected-edge",Reject<ArgumentException>(()=>cache.Minimum([4,-2],[(0,2)],q)));
    Check("recover-after-invalid",cache.Minimum([4,-2],e,q)==-2&&cache.Builds==4);
    Check("pre-cancelled",Reject<OperationCanceledException>(()=>cache.Minimum([4,-2],[],q,new CancellationToken(true))));
    Check("recover-after-cancel",cache.Minimum([4,-2],e,q)==-2&&cache.Builds==4);
    Check("numeric-overflow",Reject<OverflowException>(()=>cache.Minimum([long.MaxValue,1],e,q)));
    Check("recover-after-overflow",cache.Minimum([4,-2],e,q)==-2&&cache.Builds==4);
    Check("empty-domain",Reject<ArgumentOutOfRangeException>(()=>cache.Minimum([],[],new([],0))));
    // Distinct session instances, no shared mutable cache. This is not same-instance concurrent qualification.
    var ok=Enumerable.Range(0,32).AsParallel().All(i=>{
        var session=new CachedQuotaResidual();
        return session.Minimum([i,-i],[(0,1)],new([true,false],1))==i
            &&session.Minimum([-i,i],[(0,1)],new([true,false],1))==-i;
    });
    Check("concurrent-isolated-instances",ok);
    return;
}
if(args is not ["native"])throw new ArgumentException("native|boundary");
foreach(int n in new[]{64,512,4096})
for(int history=0;history<4;history++)
{
    int seed=856301+history;var rng=new Random(seed+n);
    var cache=new CachedQuotaResidual();
    var edges=new List<(int,int)>();var q=new bool[n];var planted=new bool[n];
    for(int root=0;root<n;)
    {
        int size=Math.Min(n-root,rng.Next(1,11));planted[root]=rng.Next(2)==0;
        for(int j=1;j<size;j++){int parent=root+rng.Next(j);edges.Add((parent,root+j));planted[root+j]=!planted[parent];}
        root+=size;
    }
    for(int i=0;i<n;i++)q[i]=rng.Next(2)==0;
    int k=Enumerable.Range(0,n).Count(i=>q[i]&&planted[i]);
    var original=edges.ToArray();var current=original.ToArray();
    for(int revision=0;revision<8;revision++)
    {
        if(revision==2 && current.Length>0)current[0]=(0,0);
        if(revision==3)current=original.ToArray();
        if(revision==4)Array.Reverse(current);
        if(revision==5)q[0]=!q[0];
        if(revision==6){k=Math.Min(n,k+1);current=current.Skip(1).ToArray();}
        if(revision==7)current=current.Concat(current.Take(1)).ToArray();
        var d=Enumerable.Range(0,n).Select(_=>(long)rng.Next(-80,81)).ToArray();
        var row=new CardinalityResidual.Row(q,k);
        int oldHits=cache.Hits;long t=Stopwatch.GetTimestamp();
        var value=cache.Minimum(d,current,row);
        long cacheTicks=Stopwatch.GetTimestamp()-t;
        var direct=NormalizedQuota.Minimum(d,current,row);
        var cp=new CpModel();var x=Enumerable.Range(0,n).Select(i=>cp.NewBoolVar("x"+i)).ToArray();
        foreach(var(a,b) in current)cp.Add(x[a]+x[b]==1);
        cp.Add(LinearExpr.Sum(x.Where((_,i)=>q[i]))==k);
        cp.Minimize(LinearExpr.WeightedSum(x,d));
        var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:856301 max_time_in_seconds:2"};
        var status=solver.Solve(cp);
        long? expected=null;bool conclusive=status is CpSolverStatus.Optimal or CpSolverStatus.Infeasible;
        if(status==CpSolverStatus.Optimal)
        {
            var bits=x.Select(solver.Value).ToArray();
            expected=Enumerable.Range(0,n).Sum(i=>bits[i]*d[i]);
            double a=Math.Abs(solver.ObjectiveValue-expected.Value),b=Math.Abs(solver.BestObjectiveBound-expected.Value);
            if(bits.Any(z=>z is not(0 or 1))||current.Any(e=>bits[e.Item1]+bits[e.Item2]!=1)||
                Enumerable.Range(0,n).Where(i=>q[i]).Sum(i=>bits[i])!=k||!double.IsFinite(a)||!double.IsFinite(b)||a>1e-6||b>1e-6)throw new Exception("native witness");
        }
        bool valid=value==direct&&(!conclusive||value==expected);
        Console.WriteLine(JsonSerializer.Serialize(new{kind="history",n,seed,revision,k,edges=current.Length,hit=cache.Hits>oldHits,status=status.ToString(),conclusive,value,direct,expected,valid,cacheTicks,frequency=Stopwatch.Frequency}));
        if(!valid||status==CpSolverStatus.ModelInvalid)throw new Exception("native mismatch");
    }
}
