using AlgorithmDiscoveryLab;
using Google.OrTools.Sat;
using System.Text.Json;

void Check(string name, bool valid)
{
    Console.WriteLine(JsonSerializer.Serialize(new{kind="boundary",name,valid}));
    if(!valid)throw new Exception(name);
}
bool Reject<T>(Action call) where T:Exception
{
    try{call();return false;}catch(T){return true;}
}
if(args is ["boundary"])
{
    Check("large-signed-delta",NormalizedQuota.Minimum([long.MaxValue-2,-1],[],new([true,true],1))==-1);
    Check("large-positive-exact",NormalizedQuota.Minimum([long.MaxValue-2,-1],[],new([true,true],2))==long.MaxValue-3);
    Check("long-min-rejected",Reject<OverflowException>(()=>NormalizedQuota.Minimum([long.MinValue],[],new([true],0))));
    Check("sentinel-rejected",Reject<OverflowException>(()=>NormalizedQuota.Minimum([long.MaxValue],[],new([true],0))));
    Check("aggregate-overflow-rejected",Reject<OverflowException>(()=>NormalizedQuota.Minimum([long.MaxValue-1,1],[],new([true,true],1))));
    Check("empty-domain-rejected",Reject<ArgumentOutOfRangeException>(()=>NormalizedQuota.Minimum([],[],new([],0))));
    Check("unknown-endpoint-rejected",Reject<ArgumentException>(()=>NormalizedQuota.Minimum([1],[(0,1)],new([true],0))));
    Check("bad-quota-length",Reject<ArgumentException>(()=>NormalizedQuota.Minimum([1],[],new([],0))));
    Check("bad-quota-count",Reject<ArgumentException>(()=>NormalizedQuota.Minimum([1],[],new([true],2))));
    Check("cancelled",Reject<OperationCanceledException>(()=>NormalizedQuota.Minimum([1],[],new([true],0),new CancellationToken(true))));
    Check("odd-cycle",NormalizedQuota.Minimum([1,2,3],[(0,1),(1,2),(2,0)],new([true,true,true],1))==null);
    Check("gcd-infeasible",NormalizedQuota.Minimum([1,2,3,4],[(0,1),(0,2),(0,3)],new([false,true,true,true],1))==null);
    Check("duplicate-order",NormalizedQuota.Minimum([1,-2,3],[(0,1),(1,2),(0,1)],new([true,true,true],2))==4);
    return;
}
if(args is not ["native"])throw new ArgumentException("native|boundary");
foreach(int n in new[]{64,512,4096})
for(int j=0;j<12;j++)
{
    int seed=963721+j;
    var rng=new Random(seed+n);
    var d=Enumerable.Range(0,n).Select(_=>(long)rng.Next(-100,101)).ToArray();
    var q=new bool[n];var planted=new bool[n];var edges=new List<(int,int)>();
    for(int root=0;root<n;){
        int size=Math.Min(n-root,rng.Next(1,13));
        planted[root]=rng.Next(2)==0;
        for(int a=1;a<size;a++){
            int parent=root+rng.Next(a);edges.Add((parent,root+a));planted[root+a]=!planted[parent];
        }
        root+=size;
    }
    for(int i=0;i<n;i++)q[i]=rng.Next(2)==0;
    int k=Enumerable.Range(0,n).Count(i=>q[i]&&planted[i]);
    if(j%3==1)k=rng.Next(n+1);
    if(j%3==2)edges.Add((0,0));
    var row=new CardinalityResidual.Row(q,k);
    var candidate=NormalizedQuota.Minimum(d,edges,row);
    var baseline=CardinalityResidual.Minimum(d,edges,row);
    var cp=new CpModel();var x=Enumerable.Range(0,n).Select(i=>cp.NewBoolVar("x"+i)).ToArray();
    foreach(var(a,b) in edges)cp.Add(x[a]+x[b]==1);
    cp.Add(LinearExpr.Sum(x.Where((_,i)=>q[i]))==k);
    cp.Minimize(LinearExpr.WeightedSum(x,d));
    var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:963721 max_time_in_seconds:2"};
    var status=solver.Solve(cp);
    long? native=null;bool conclusive=status is CpSolverStatus.Optimal or CpSolverStatus.Infeasible;
    if(status==CpSolverStatus.Optimal){
        var bits=x.Select(solver.Value).ToArray();
        native=Enumerable.Range(0,n).Sum(i=>bits[i]*d[i]);
        double oe=Math.Abs(solver.ObjectiveValue-native.Value),be=Math.Abs(solver.BestObjectiveBound-native.Value);
        if(bits.Any(v=>v is not(0 or 1))||edges.Any(e=>bits[e.Item1]+bits[e.Item2]!=1)||
           Enumerable.Range(0,n).Where(i=>q[i]).Sum(i=>bits[i])!=k||
           !double.IsFinite(oe)||!double.IsFinite(be)||oe>1e-6||be>1e-6)throw new Exception("Native witness/report mismatch");
    }
    bool valid=candidate==baseline&&(!conclusive||candidate==native);
    Console.WriteLine(JsonSerializer.Serialize(new{kind="native",n,seed,k,edges=edges.Count,status=status.ToString(),conclusive,candidate,baseline,native,valid}));
    if(!valid||status==CpSolverStatus.ModelInvalid)throw new Exception("Native disagreement");
}
