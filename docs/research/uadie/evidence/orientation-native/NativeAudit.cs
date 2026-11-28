using AlgorithmDiscoveryLab;
using ComponentStudy;
using Google.OrTools.Sat;
using System.Diagnostics;
using System.Text.Json;

static (int? Value,int Mask) SolveNative(Model model,int? hint)
{
    var cp=new CpModel();
    var vars=Enumerable.Range(0,model.Size).Select(i=>cp.NewBoolVar("b"+i)).ToArray();
    foreach(var r in model.Capacities)
        cp.AddLinearConstraint(LinearExpr.Sum(vars.Where((_,i)=>(r.Scope & (1<<i))!=0)),r.Lower,r.Upper);
    cp.Minimize(LinearExpr.WeightedSum(vars,model.Costs.Select(x=>(long)x.Weight)));
    if(hint is int h)for(int i=0;i<vars.Length;i++)cp.AddHint(vars[i],(h>>i)&1);
    var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:575001 max_time_in_seconds:2"};
    var status=solver.Solve(cp);
    if(status==CpSolverStatus.Infeasible)return(null,0);
    if(status!=CpSolverStatus.Optimal)throw new Exception("Native status: "+status);
    int mask=0;
    for(int i=0;i<vars.Length;i++)if(solver.Value(vars[i])==1)mask|=1<<i;
    int val=checked((int)solver.ObjectiveValue);
    if(solver.ObjectiveValue!=val || solver.BestObjectiveBound!=val)throw new Exception(JsonSerializer.Serialize(new {message="Native objective bound mismatch", model, mask, hint, reportedObjective=solver.ObjectiveValue, reportedBound=solver.BestObjectiveBound, truncated=val, exactWitness=model.Costs.Where((_,i)=>(mask&(1<<i))!=0).Sum(c=>(long)c.Weight)}));
    return(val,mask);
}
static bool Verify(Model model,int? value,int mask,int? expected)
{
    if(value!=expected)return false;
    if(value==null)return true;
    if(mask<0 || mask>=Math.Pow(2,model.Size))return false;
    var selected=Enumerable.Range(0,model.Size).Where(i=>((mask/(1<<i))%2)==1).ToHashSet();
    if(selected.Sum(i=>(long)model.Costs[i].Weight)!=value)return false;
    return model.Capacities.All(r=>{
        var count=selected.Count(i=>((r.Scope/(1<<i))%2)==1);
        return r.Lower<=count && count<=r.Upper;
    });
}
static ICandidate Create(string name)=>name switch{
    "full"=>new FullSearch(false),"warm"=>new FullSearch(true),"partition"=>new Components(false),"reuse"=>new Components(true),
    "box"=>new Transfer(false),"pair"=>new Transfer(true),"parity"=>new ParityTransfer(),"parity-warm"=>new ParityTransfer(true),
    "orientation"=>new OrientationSearch(false),"orientation-adaptive"=>new OrientationSearch(true),_=>throw new ArgumentException(name)};
string[] arms=["full","warm","partition","reuse","box","pair","parity","parity-warm","orientation","orientation-adaptive"];
int cases=0,checks=0;
void Compare(Model[] sequence,string family,int seed,int rep)
{
    var candidates=arms.ToDictionary(a=>a,Create);
    int? hint=null;
    for(int j=0;j<sequence.Length;j++)
    {
        var model=sequence[j];
        long t=Stopwatch.GetTimestamp(); var cold=SolveNative(model,null);
        if(!Verify(model,cold.Value,cold.Mask,cold.Value))throw new Exception("Native witness failure");
        long coldTicks=Stopwatch.GetTimestamp()-t;
        t=Stopwatch.GetTimestamp(); var warm=SolveNative(model,hint);
        if(!Verify(model,warm.Value,warm.Mask,cold.Value))throw new Exception("Native hint failure");
        long warmTicks=Stopwatch.GetTimestamp()-t;
        hint=warm.Value.HasValue?warm.Mask:null;
        // The separate truth table provides infeasibility and optimum checks
        // independent of both native search and all candidate algorithms.
        int? exhaustive=Oracle.Minimum(model);
        if(exhaustive!=cold.Value)throw new Exception(JsonSerializer.Serialize(new {model,cold,exhaustive}));
        cases++;
        foreach(var name in arms.Skip(rep%arms.Length).Concat(arms.Take(rep%arms.Length)))
        {
            t=Stopwatch.GetTimestamp();
            var result=candidates[name].Solve(model);
            bool valid=Verify(model,result.Best?.Value,result.Best?.Mask??0,cold.Value);
            long ticks=Stopwatch.GetTimestamp()-t;
            if(!valid)throw new Exception(JsonSerializer.Serialize(new {model,result,cold,name}));
            Console.WriteLine(JsonSerializer.Serialize(new {kind="small",family,seed,rep,revision=j,n=model.Size,arm=name,value=cold.Value,valid,ticks,coldTicks,warmTicks,frequency=Stopwatch.Frequency}));
            checks++;
        }
    }
}
if(args is ["small"] or ["diagnose"])
{
    string[] families=["chain-shift","star-shift","cycle-shift","odd-cycle","mixed","relax-chain","disjoint","isolated","tighten","large-delta"];
    for(int rep=0;args[0]=="small" && rep<3;rep++)foreach(string f in families)for(int i=0;i<8;i++){
        var p=ParityCorpus.Generate(f,485000+i,8+i%5); Compare([p.Source,p.Revision],f,485000+i,rep);}
    // Independently constructed histories: arbitrary cardinality rows on random
    // nonadjacent scopes, duplicates, row reorder, deletion, restored source.
    for(int seed=575001;seed<(args[0]=="diagnose"?575002:575129);seed++){
        var rng=new Random(seed);int n=rng.Next(3,11);var rows=new List<Capacity>();
        for(int k=0;k<rng.Next(1,9);k++){
            int scope=rng.Next(1,1<<n);int width=System.Numerics.BitOperations.PopCount((uint)scope);
            int lo=rng.Next(width+1);int hi=rng.Next(lo,width+1);rows.Add(new(scope,lo,hi));}
        for(int k=0;k<3;k++){int a=rng.Next(n), b=rng.Next(n);if(a!=b)rows.Add(new((1<<a)|(1<<b),1,1));}
        var p=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,rng.Next(-50,51))).ToArray();
        var old=new Model(n,rows.ToArray(),p);
        var change=old with {Costs=p.Select(c=>c with {Weight=c.Weight+rng.Next(-15,16)}).ToArray(),
            Capacities=rows.Reverse<Capacity>().Concat(rows.Take(1)).ToArray()};
        var relax=change with{Capacities=change.Capacities.Skip(2).ToArray()};
        Compare([old,change,relax,old],"independent-history",seed,0);
    }
    Console.Error.WriteLine(JsonSerializer.Serialize(new{cases,checks,failures=0}));return;
}
if(args is not ["wide"])throw new ArgumentException("small|wide");
// Wide pair-only model: exact solution follows from independent orientations,
// but this is NOT a general integer/cardinality solver.
foreach(int n in new[]{64,256,1024,4096})
foreach(string f in new[]{"chain","blocks","sparse","odd"})
for(int rep=0;rep<3;rep++)
{
    var rng=new Random(791000+n+rep);
    var edges=new List<(int A,int B)>();
    int block=f=="blocks"?16:f=="sparse"?2:n;
    for(int i=1;i<n;i++)if(i%block!=0)edges.Add((i-1,i));
    if(f=="odd")edges.Add((0,2));
    var cost=Enumerable.Range(0,n).Select(_=>rng.Next(-1000,1001)).ToArray();
    long t=Stopwatch.GetTimestamp();
    var parts=OrientationPartition.Build(n,edges);
    bool[]? assignment=parts==null?null:new bool[n];
    long? value=parts==null?null:0;
    if(parts!=null)foreach(var p in parts){
        long a=p.Zero.Sum(i=>(long)cost[i]),b=p.One.Sum(i=>(long)cost[i]);
        foreach(int v in a<=b?p.Zero:p.One)assignment![v]=true;
        value=checked(value!.Value+Math.Min(a,b));
    }
    if(assignment!=null && (edges.Any(e=>assignment[e.A]==assignment[e.B]) ||
        Enumerable.Range(0,n).Where(i=>assignment[i]).Sum(i=>(long)cost[i])!=value))throw new Exception("Wide witness invalid");
    long localTicks=Stopwatch.GetTimestamp()-t;
    t=Stopwatch.GetTimestamp();
    var cp=new CpModel();var x=Enumerable.Range(0,n).Select(i=>cp.NewBoolVar("v"+i)).ToArray();
    foreach(var (a,b) in edges)cp.Add(x[a]+x[b]==1);
    cp.Minimize(LinearExpr.WeightedSum(x,cost.Select(c=>(long)c)));
    var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:791001 max_time_in_seconds:2"};
    var status=solver.Solve(cp);
    long? expected=status==CpSolverStatus.Infeasible?null:status==CpSolverStatus.Optimal?checked((long)solver.ObjectiveValue):throw new Exception("Wide native status: "+status);
    if(expected!=null){
        var bits=x.Select(v=>solver.Value(v)).ToArray();
        if(edges.Any(e=>bits[e.A]+bits[e.B]!=1)||Enumerable.Range(0,n).Sum(i=>bits[i]*cost[i])!=expected||
            solver.BestObjectiveBound!=expected || solver.ObjectiveValue!=expected)throw new Exception("Wide native witness");
    }
    long nativeTicks=Stopwatch.GetTimestamp()-t;
    if(value!=expected)throw new Exception("Wide disagreement");
    Console.WriteLine(JsonSerializer.Serialize(new{kind="wide",n,f,rep,edges=edges.Count,components=parts?.Count,value,expected,valid=true,localTicks,nativeTicks,frequency=Stopwatch.Frequency}));
}
