using AlgorithmDiscoveryLab;
using ComponentStudy;
using Google.OrTools.Sat;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length!=1)throw new ArgumentException("fresh evidence directory");
string root=args[0];Directory.CreateDirectory(root);
string[] names=["warm","propagation","structural"];
var files=names.ToDictionary(x=>x,x=>new FileStream(Path.Combine(root,x+".jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read));
try
{
foreach(int n in new[]{8,12})
foreach(string family in new[]{"availability","odd-cycle","random-pairs","packing","shift-cover","unconstrained"})
for(int h=0;h<4;h++)
{
    int seed=1039101+h;var random=new Random(seed+n);
    int[] perm=Enumerable.Range(0,n).OrderBy(_=>random.Next()).ToArray();
    int Bit(int j)=>1<<perm[j];
    int all=(1<<n)-1;
    var rows=new List<Capacity>();
    if(family=="availability"||family=="shift-cover"||family=="random-pairs")
        for(int i=0;i<n-1;i++)
        {
            int a=random.Next(n),b=random.Next(n-1);if(b>=a)b++;
            rows.Add(new(Bit(a)|Bit(b),1,1));
        }
    if(family=="odd-cycle")
    {
        rows.Add(new(Bit(0)|Bit(1),1,1));rows.Add(new(Bit(1)|Bit(2),1,1));rows.Add(new(Bit(0)|Bit(2),1,1));
    }
    if(family=="availability")rows.Add(new(Bit(0)|Bit(3),0,0));
    if(family=="shift-cover")rows.Add(new(all,n/2,n/2));
    if(family=="packing")
        for(int j=0;j<n;j++){int scope=random.Next(1,all+1);int w=BitOperations.PopCount((uint)scope);rows.Add(new(scope,0,random.Next(w+1)));}
    var prices=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,random.Next(-100,101))).ToArray();
    var arms=new Dictionary<string,ICandidate>{{"warm",new FullSearch(true)},{"propagation",new PropagationSearch()},{"structural",new StructuralAdmission(true)}};
    for(int rev=0;rev<6;rev++)
    {
        if(rev>0){int j=random.Next(n);prices[j]=prices[j] with {Weight=prices[j].Weight+random.Next(-50,51)};}
        if(rev==2)rows.Add(new(Bit(0),0,0));
        if(rev==3)rows.Add(new(Bit(0),1,1));
        if(rev==4){rows.RemoveAt(rows.Count-1);rows.Reverse();}
        if(rev==5)rows.Add(new(Bit(1)|Bit(2)|Bit(3),1,2));
        var input=new Model(n,rows.ToArray(),prices.ToArray());
        var cp=new CpModel();var x=Enumerable.Range(0,n).Select(i=>cp.NewBoolVar("x"+i)).ToArray();
        foreach(var r in input.Capacities)
        {
            var sum=LinearExpr.Sum(x.Where((_,i)=>(r.Scope&(1<<i))!=0));
            cp.AddLinearConstraint(sum,r.Lower,r.Upper);
        }
        cp.Minimize(LinearExpr.WeightedSum(x,prices.Select(c=>(long)c.Weight)));
        var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:1039101 max_time_in_seconds:2"};
        var status=solver.Solve(cp);
        bool conclusive=status is CpSolverStatus.Optimal or CpSolverStatus.Infeasible;
        int? native=null;
        if(status==CpSolverStatus.Optimal)
        {
            int mask=0;for(int i=0;i<n;i++){long b=solver.Value(x[i]);if(b is not(0 or 1))throw new Exception("invalid native bit");if(b==1)mask|=1<<i;}
            native=Oracle.Evaluate(input,mask);
            if(native==null||!double.IsFinite(solver.ObjectiveValue)||!double.IsFinite(solver.BestObjectiveBound)||Math.Abs(solver.ObjectiveValue-native.Value)>1e-6||Math.Abs(solver.BestObjectiveBound-native.Value)>1e-6)throw new Exception("native witness");
        }
        int? oracle=Oracle.Minimum(input);
        foreach(string name in names.Skip((h+rev)%names.Length).Concat(names.Take((h+rev)%names.Length)))
        {
            long start=Stopwatch.GetTimestamp();
            var m=AdaptiveDecisionStudy.Admit(input);
            string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m)));
            var result=arms[name].Solve(m);
            int? cost=result.Best is {} b?Oracle.Evaluate(m,b.Mask):null;
            bool valid=result.Best?.Value==oracle&&cost==oracle&&(!conclusive||native==oracle);
            var ev=JsonSerializer.SerializeToUtf8Bytes(new{digest,n,family,seed,revision=rev,result.Best});
            long prepared=Stopwatch.GetTimestamp();
            var f=files[name];f.Write(ev);f.WriteByte(10);f.Flush(true);
            long end=Stopwatch.GetTimestamp();
            Console.WriteLine(JsonSerializer.Serialize(new{n,family,seed,revision=rev,name,digest,model=input,value=result.Best?.Value,oracle,native,status=status.ToString(),conclusive,valid,route=(arms[name] as StructuralAdmission)?.Route??name,computeTicks=prepared-start,flushTicks=end-prepared,ticks=end-start,frequency=Stopwatch.Frequency}));
            if(!valid||status==CpSolverStatus.ModelInvalid)throw new Exception("native or exhaustive mismatch");
        }
    }
}
}
finally{foreach(var f in files.Values)f.Dispose();}
