using AlgorithmDiscoveryLab;
using Google.OrTools.Sat;
using System.Diagnostics;
using System.Text.Json;

if(args is ["audit"]){Audit();return;}
if(args is ["adaptive"]){Adaptive();return;}
if(args is ["incremental",var split]){IncrementalStudy.Run(split,Original,m=>Quotient(m),(m,t)=>Select(m,t).Result,Check);return;}
if(args.Length!=1)throw new ArgumentException("dev|holdout|audit");
bool dev=args[0]=="dev";if(!dev&&args[0]!="holdout")throw new ArgumentException("split");
foreach(int n in dev?new[]{64,512}:new[]{64,512,4096,10000})
foreach(string family in dev?new[]{"pair","linked"}:new[]{"pair","sparse-quota","linked"})
for(int h=0;h<(dev?1:2);h++)
{
    int seed=(dev?1290101:1320101)+h;var rng=new Random(seed+n);
    var edges=new List<(int A,int B)>();var planted=new int[n];
    var roots=new List<int>();
    for(int start=0;start<n;)
    {
        int width=Math.Min(n-start,rng.Next(3,10));roots.Add(start);
        planted[start]=rng.Next(2);
        for(int j=1;j<width;j++){int parent=start+rng.Next(j);edges.Add((parent,start+j));planted[start+j]=1-planted[parent];}
        start+=width;
    }
    var fixedBits=new List<(int Bit,int Value)>();
    for(int i=0;i<roots.Count;i+=7)fixedBits.Add((roots[i],planted[roots[i]]));
    var rows=new List<WideRow>();
    if(family=="sparse-quota")
        for(int i=0;i<roots.Count;i+=16)
        {
            int[] scope=roots.Skip(i).Take(16).ToArray();int target=scope.Sum(j=>planted[j]);
            rows.Add(new(scope,target,target));
        }
    if(family=="linked")
        for(int i=0;i<roots.Count-1;i++)
        {
            int[] scope=[roots[i],roots[i+1]];int v=scope.Sum(j=>planted[j]);
            rows.Add(new(scope,Math.Max(0,v-1),Math.Min(2,v+1)));
        }
    for(int rev=0;rev<3;rev++)
    {
        var d=Enumerable.Range(0,n).Select(_=>(long)rng.Next(-100,101)).ToArray();
        var current=edges.ToArray();
        var fixes=fixedBits.ToArray();
        if(rev==1)Array.Reverse(current);
        if(rev==2)
        {
            // New constraint fixes a planted value. Some models have no feasible old optimum after this edit.
            fixes=fixes.Append((n-1,planted[n-1])).ToArray();
        }
        var m=new WideModel(n,current,fixes,rows.ToArray(),d);
        var results=new Dictionary<string,WideResult>();
        foreach(string arm in (h+rev)%2==0?new[]{"original","quotient"}:new[]{"quotient","original"})
        {
            long t=Stopwatch.GetTimestamp();
            var result=arm=="original"?Original(m):Quotient(m);
            long solved=Stopwatch.GetTimestamp();
            long? witness=result.Bits==null?null:Check(m,result.Bits);
            if(result.Bits!=null&&witness!=result.Value)throw new Exception("bad reconstructed witness");
            if(result.Status=="Optimal"&&result.Bits==null)throw new Exception("missing optimum");
            if(result.Status is "ModelInvalid" or "Invalid")throw new Exception("bad model");
            var encoded=JsonSerializer.SerializeToUtf8Bytes(new{n,family,seed,rev,result.Status,result.Value,result.Bits});
            long end=Stopwatch.GetTimestamp();
            results.Add(arm,result);
            Console.WriteLine(JsonSerializer.Serialize(new{split=args[0],n,family,seed,revision=rev,arm,status=result.Status,value=result.Value,variables=result.Variables,rows=result.Rows,verifiedWitness=result.Bits!=null,solveTicks=solved-t,ticks=end-t,frequency=Stopwatch.Frequency,evidenceBytes=encoded.Length}));
        }
        var a=results["original"];var b=results["quotient"];
        bool ca=a.Status is "Optimal" or "Infeasible",cb=b.Status is "Optimal" or "Infeasible";
        if(ca&&cb&&(a.Status!=b.Status||a.Value!=b.Value))throw new Exception("conclusive mismatch");
        if((a.Status=="Infeasible"&&b.Bits!=null)||(b.Status=="Infeasible"&&a.Bits!=null))throw new Exception("feasibility mismatch");
        if(a.Status=="Optimal"&&b.Value<a.Value||b.Status=="Optimal"&&a.Value<b.Value)throw new Exception("better than proven optimum");
    }
}

static long Check(WideModel m,int[] x)
{
    if(x.Length!=m.N||x.Any(v=>v is not(0 or 1)))throw new Exception("domain");
    foreach(var (a,b) in m.Edges)if(x[a]+x[b]!=1)throw new Exception("edge");
    foreach(var (bit,v) in m.Fixes)if(x[bit]!=v)throw new Exception("fix");
    foreach(var r in m.Rows){int count=r.Scope.Sum(i=>x[i]);if(count<r.L||count>r.U)throw new Exception("row");}
    long total=0;for(int i=0;i<m.N;i++)total+=m.Cost[i]*x[i];return total;
}
static CpSolver Solver()=>new(){StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};
static void Reports(CpSolver solver,long value,bool optimal)
{
    if(!double.IsFinite(solver.ObjectiveValue)||Math.Abs(solver.ObjectiveValue-value)>1e-6)throw new Exception("objective report");
    if(optimal&&(!double.IsFinite(solver.BestObjectiveBound)||Math.Abs(solver.BestObjectiveBound-value)>1e-6))throw new Exception("bound report");
}
static WideResult Original(WideModel m)
{
    var cp=new CpModel();var x=Enumerable.Range(0,m.N).Select(i=>cp.NewBoolVar("x"+i)).ToArray();
    foreach(var(a,b) in m.Edges)cp.Add(x[a]+x[b]==1);
    foreach(var(bit,value) in m.Fixes)cp.Add(x[bit]==value);
    foreach(var r in m.Rows)cp.AddLinearConstraint(LinearExpr.Sum(r.Scope.Select(i=>x[i])),r.L,r.U);
    cp.Minimize(LinearExpr.WeightedSum(x,m.Cost));
    var solver=Solver();var status=solver.Solve(cp);
    if(status is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(status.ToString(),null,null,m.N,m.Edges.Length+m.Fixes.Length+m.Rows.Length);
    var bits=x.Select(v=>checked((int)solver.Value(v))).ToArray();long cost=Check(m,bits);
    Reports(solver,cost,status==CpSolverStatus.Optimal);
    return new(status.ToString(),cost,bits,m.N,m.Edges.Length+m.Fixes.Length+m.Rows.Length);
}
static WideResult Quotient(WideModel m,List<(int[] Zero,int[] One)>? supplied=null,PhaseTrace? trace=null)
{
    var partition=supplied??OrientationPartition.Build(m.N,m.Edges);trace?.Mark("g12-quotient-partition");
    if(partition==null)return new("Infeasible",null,null,0,0);
    int[] owner=new int[m.N],side=new int[m.N],fixedChoice=new int[partition.Count];
    Array.Fill(fixedChoice,-1);
    for(int j=0;j<partition.Count;j++)
    {
        foreach(int i in partition[j].Zero){owner[i]=j;side[i]=0;}
        foreach(int i in partition[j].One){owner[i]=j;side[i]=1;}
    }
    foreach(var (i,value) in m.Fixes)
    {
        int y=value==1?side[i]:1-side[i];
        if(fixedChoice[owner[i]]>=0&&fixedChoice[owner[i]]!=y)return new("Infeasible",null,null,0,0);
        fixedChoice[owner[i]]=y;
    }
    trace?.Mark("g12-duplicate-owner-fix");
    var cp=new CpModel();var free=new Dictionary<int,BoolVar>();
    long offset=0;var objVars=new List<BoolVar>();var objCost=new List<long>();
    for(int j=0;j<partition.Count;j++)
    {
        long a=partition[j].Zero.Sum(i=>m.Cost[i]),b=partition[j].One.Sum(i=>m.Cost[i]);
        if(fixedChoice[j]>=0){offset+=fixedChoice[j]==0?a:b;continue;}
        var y=cp.NewBoolVar("y"+j);free[j]=y;offset+=a;objVars.Add(y);objCost.Add(b-a);
    }
    int nr=0;
    foreach(var r in m.Rows)
    {
        int baseline=0;var coeff=new Dictionary<int,long>();
        foreach(int i in r.Scope)
        {
            int j=owner[i];
            if(fixedChoice[j]>=0){if(fixedChoice[j]==side[i])baseline++;continue;}
            if(side[i]==0)baseline++;
            coeff[j]=coeff.GetValueOrDefault(j)+(side[i]==0?-1:1);
        }
        long low=baseline+coeff.Values.Sum(x=>Math.Min(0,x)),high=baseline+coeff.Values.Sum(x=>Math.Max(0,x));
        if(low>r.U||high<r.L)return new("Infeasible",null,null,free.Count,nr);
        if(low>=r.L&&high<=r.U)continue;
        var active=coeff.Where(x=>x.Value!=0).ToArray();
        cp.AddLinearConstraint(LinearExpr.WeightedSum(active.Select(x=>free[x.Key]),active.Select(x=>x.Value)),r.L-baseline,r.U-baseline);
        nr++;
    }
    cp.Minimize(LinearExpr.WeightedSum(objVars,objCost)+offset);trace?.Mark("g12-native-build");
    var solver=Solver();var status=solver.Solve(cp);trace?.Mark("g12-native-solve");
    if(status is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(status.ToString(),null,null,free.Count,nr);
    var bits=new int[m.N];
    for(int j=0;j<partition.Count;j++)
    {
        int y=fixedChoice[j]>=0?fixedChoice[j]:checked((int)solver.Value(free[j]));
        if(y is not(0 or 1))throw new Exception("quotient domain");
        foreach(int i in y==0?partition[j].Zero:partition[j].One)bits[i]=1;
    }
    trace?.Mark("g12-reconstruct");long cost=Check(m,bits);Reports(solver,cost,status==CpSolverStatus.Optimal);
    return new(status.ToString(),cost,bits,free.Count,nr);
}

static void Audit()
{
    var options=new JsonSerializerOptions{IncludeFields=true};
    foreach(int n in new[]{64,512,4096,10000})
    foreach(string family in new[]{"uncompressed","sparse-matching","odd-cycle","opposite-fixes","impossible-row","overlapping"})
    for(int h=0;h<2;h++)
    {
        int seed=1457101+h;var rng=new Random(seed+n);
        var edges=new List<(int A,int B)>();
        var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
        if(family=="sparse-matching"||family=="overlapping"||family=="opposite-fixes")
            for(int i=0;i<n/8;i++){int left=2*i,right=left+1;edges.Add((left,right));planted[right]=1-planted[left];}
        if(family=="odd-cycle"){edges.Add((0,1));edges.Add((1,2));edges.Add((2,0));}
        var fixes=new List<(int Bit,int Value)>();
        if(family=="opposite-fixes"){fixes.Add((0,0));fixes.Add((1,0));}
        if(family=="impossible-row"){fixes.Add((0,0));fixes.Add((1,0));}
        var rows=new List<WideRow>();
        if(family=="impossible-row")rows.Add(new([0,1],1,2));
        if(family=="uncompressed"||family=="sparse-matching")
            for(int i=0;i<n;i+=8){int[] scope=Enumerable.Range(i,Math.Min(8,n-i)).ToArray();int v=scope.Sum(j=>planted[j]);rows.Add(new(scope,v,v));}
        if(family=="overlapping")
            for(int i=0;i<n-7;i+=4){int[] scope=Enumerable.Range(i,8).ToArray();int v=scope.Sum(j=>planted[j]);rows.Add(new(scope,Math.Max(0,v-1),Math.Min(8,v+1)));}
        bool expectedInfeasible=family is "odd-cycle" or "opposite-fixes" or "impossible-row";
        var model=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-100,101)).ToArray());
        if(!expectedInfeasible)_=Check(model,planted);
        var results=new Dictionary<string,WideResult>();
        foreach(string arm in h%2==0?new[]{"original","quotient"}:new[]{"quotient","original"})
        {
            long t=Stopwatch.GetTimestamp();
            var r=arm=="original"?Original(model):Quotient(model);
            if(r.Bits!=null&&Check(model,r.Bits)!=r.Value)throw new Exception("witness");
            if(r.Status is "ModelInvalid" or "Invalid")throw new Exception("model invalid");
            if(expectedInfeasible&&r.Bits!=null||!expectedInfeasible&&r.Status=="Infeasible")throw new Exception("planted truth mismatch");
            var evidence=JsonSerializer.SerializeToUtf8Bytes(new{n,family,seed,r.Status,r.Value,r.Bits},options);
            long ticks=Stopwatch.GetTimestamp()-t;
            results.Add(arm,r);
            Console.WriteLine(JsonSerializer.Serialize(new{n,family,seed,arm,expectedInfeasible,status=r.Status,value=r.Value,variables=r.Variables,rows=r.Rows,verifiedWitness=r.Bits!=null,ticks,frequency=Stopwatch.Frequency,evidenceBytes=evidence.Length,model,bits=r.Bits},options));
        }
        var a=results["original"];var b=results["quotient"];
        bool ca=a.Status is "Optimal" or "Infeasible",cb=b.Status is "Optimal" or "Infeasible";
        if(ca&&cb&&(a.Status!=b.Status||a.Value!=b.Value))throw new Exception("conclusive mismatch");
        if(a.Status=="Optimal"&&b.Value<a.Value||b.Status=="Optimal"&&a.Value<b.Value)throw new Exception("bound mismatch");
    }
}


static (WideResult Result,string Route,int Free) Select(WideModel m,PhaseTrace? trace=null)
{
    // No pair graph and few fixes: no substantial quotient reduction is possible.
    if(m.Edges.Length==0 && 4L*m.Fixes.Length<m.N)
        return (Original(m),"original-fast",m.N);
    var partition=OrientationPartition.Build(m.N,m.Edges);trace?.Mark("g12-partition");
    if(partition==null)return(new("Infeasible",null,null,0,0),"odd-cycle",0);
    int[] owner=new int[m.N],side=new int[m.N];
    int[] fix=new int[partition.Count];Array.Fill(fix,-1);
    for(int j=0;j<partition.Count;j++)
    {
        foreach(int i in partition[j].Zero){owner[i]=j;side[i]=0;}
        foreach(int i in partition[j].One){owner[i]=j;side[i]=1;}
    }
    int fixedComponents=0;
    foreach(var (i,value) in m.Fixes)
    {
        int y=value==1?side[i]:1-side[i],j=owner[i];
        if(fix[j]>=0&&fix[j]!=y)return(new("Infeasible",null,null,0,0),"conflicting-fix",0);
        if(fix[j]<0){fix[j]=y;fixedComponents++;}
    }
    int free=partition.Count-fixedComponents;trace?.Mark("g12-owner-fix");
    // Frozen descriptive threshold, not a proven cost model.
    return 4L*free<=3L*m.N
        ? (Quotient(m,partition,trace),"quotient",free)
        : (Original(m),"original-low-reduction",free);
}
static void Adaptive()
{
    var options=new JsonSerializerOptions{IncludeFields=true};
    foreach(int n in new[]{512,4096,10000})
    foreach(string family in new[]{"none","weak","medium","strong","odd","fixed"})
    for(int h=0;h<3;h++)
    {
        int seed=1619301+h;var rng=new Random(seed+n);
        var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
        var edges=new List<(int A,int B)>();var fixes=new List<(int Bit,int Value)>();var rows=new List<WideRow>();
        int group=family switch{"weak"=>2,"medium"=>3,"strong"=>8,_=>1};
        int covered=family=="weak"?n/5:family=="medium"?n*3/4:family=="strong"?n:0;
        for(int i=0;i<covered;i+=group)
        for(int j=i+1;j<Math.Min(i+group,covered);j++){edges.Add((i,j));planted[j]=1-planted[i];}
        if(family=="odd"){edges.Add((0,1));edges.Add((1,2));edges.Add((2,0));}
        if(family=="fixed")for(int i=0;i<n;i+=2)fixes.Add((i,planted[i]));
        for(int i=0;i<n;i+=12)
        {
            int[] scope=Enumerable.Range(i,Math.Min(12,n-i)).ToArray();int target=scope.Sum(j=>planted[j]);
            rows.Add(new(scope,target,target));
        }
        var model=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-90,91)).ToArray());
        bool impossible=family=="odd";
        if(!impossible)_=Check(model,planted);
        string[] arms=["original","quotient","adaptive"];
        var results=new Dictionary<string,WideResult>();
        for(int k=0;k<3;k++)
        {
            string arm=arms[(h+k)%3];long t=Stopwatch.GetTimestamp();
            var selected=arm=="adaptive"?Select(model):(arm=="original"?Original(model):Quotient(model),arm,-1);
            var (result,route,free)=selected;
            if(result.Status is "ModelInvalid" or "Invalid")throw new Exception("model invalid");
            if(result.Bits!=null&&Check(model,result.Bits)!=result.Value)throw new Exception("witness");
            if(impossible&&result.Bits!=null||!impossible&&result.Status=="Infeasible")throw new Exception("planted truth mismatch");
            var evidence=JsonSerializer.SerializeToUtf8Bytes(new{n,family,seed,result.Status,result.Value,result.Bits},options);
            long ticks=Stopwatch.GetTimestamp()-t;results.Add(arm,result);
            Console.WriteLine(JsonSerializer.Serialize(new{n,family,seed,arm,route,free,status=result.Status,value=result.Value,variables=result.Variables,verifiedWitness=result.Bits!=null,ticks,frequency=Stopwatch.Frequency,evidenceBytes=evidence.Length,model,bits=result.Bits},options));
        }
        foreach(var a in results.Values)foreach(var b in results.Values)
        {
            bool ca=a.Status is "Optimal" or "Infeasible",cb=b.Status is "Optimal" or "Infeasible";
            if(ca&&cb&&(a.Status!=b.Status||a.Value!=b.Value))throw new Exception("conclusive mismatch");
            if(a.Status=="Optimal"&&b.Value<a.Value)throw new Exception("bound mismatch");
        }
    }
}

internal record WideRow(int[] Scope,int L,int U);
internal record WideModel(int N,(int A,int B)[] Edges,(int Bit,int Value)[] Fixes,WideRow[] Rows,long[] Cost);
internal record WideResult(string Status,long? Value,int[]? Bits,int Variables,int Rows);
