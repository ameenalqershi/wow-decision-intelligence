using AlgorithmDiscoveryLab;
using Google.OrTools.Sat;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

internal sealed class PhaseTrace
{
    private long last=Stopwatch.GetTimestamp();
    internal Dictionary<string,long> Ticks {get;}=new();
    internal void Mark(string name)
    {
        long now=Stopwatch.GetTimestamp();
        Ticks[name]=Ticks.GetValueOrDefault(name)+now-last;last=now;
    }
}

// Immutable structural snapshot owned by a single session. No mutable solver or bound cached.
internal sealed class IncrementalQuotient(bool reuse,bool template=false,bool fast=false)
{
    private sealed record Row(int[] Index,long[] Coeff,long L,long U);
    private sealed record Plan(WideModel Shape,int[] Map,int[] Base,int Free,Row[] Rows,bool Impossible,bool Direct);
    private Plan? plan;
    private CpModel? templateModel;
    private BoolVar[]? templateVariables;
    internal bool TemplateHit {get;private set;}
    private int busy;
    internal bool Hit {get;private set;}
    internal string Route {get;private set;}="";
    internal int Builds {get;private set;}
    internal static WideModel Admit(WideModel m)
    {
        if(m.N<1||m.N>100000||m.Cost.Length!=m.N)throw new ArgumentException("dimension");
        Int128 magnitude=0;foreach(long c in m.Cost)magnitude+=Int128.Abs(c);
        if(magnitude>(1L<<50))throw new OverflowException("objective exceeds exact report envelope");
        foreach(var(a,b) in m.Edges)if(a<0||a>=m.N||b<0||b>=m.N)throw new ArgumentException("edge");
        foreach(var(i,v) in m.Fixes)if(i<0||i>=m.N||v is not(0 or 1))throw new ArgumentException("fix");
        foreach(var row in m.Rows)
        {
            if(row.L<0||row.U<row.L||row.U>row.Scope.Length||row.Scope.Any(i=>i<0||i>=m.N)||row.Scope.Distinct().Count()!=row.Scope.Length)
                throw new ArgumentException("row");
        }
        return m;
    }
    private static bool Same(WideModel a,WideModel b)
    {
        if(a.N!=b.N||!a.Edges.AsSpan().SequenceEqual(b.Edges)||!a.Fixes.AsSpan().SequenceEqual(b.Fixes)||a.Rows.Length!=b.Rows.Length)return false;
        for(int j=0;j<a.Rows.Length;j++)
        {
            var x=a.Rows[j];var y=b.Rows[j];
            if(x.L!=y.L||x.U!=y.U||!x.Scope.AsSpan().SequenceEqual(y.Scope))return false;
        }
        return true;
    }
    private static Plan Build(WideModel m,PhaseTrace trace,CancellationToken ct)
    {
        var shape=new WideModel(m.N,m.Edges.ToArray(),m.Fixes.ToArray(),m.Rows.Select(r=>new WideRow(r.Scope.ToArray(),r.L,r.U)).ToArray(),[]);
        Plan No()=>new(shape,[],[],0,[],true,false);
        // Fast native bypass without graph, identical gate as G12.
        if(m.Edges.Length==0&&4L*m.Fixes.Length<m.N){trace.Mark("snapshot");return new(shape,[],[],m.N,[],false,true);}
        var part=OrientationPartition.Build(m.N,shape.Edges);trace.Mark("partition");
        if(part==null)return No();
        int[] owner=new int[m.N],side=new int[m.N],fix=new int[part.Count];Array.Fill(fix,-1);
        for(int j=0;j<part.Count;j++)
        {
            ct.ThrowIfCancellationRequested();
            foreach(int i in part[j].Zero){owner[i]=j;side[i]=0;}
            foreach(int i in part[j].One){owner[i]=j;side[i]=1;}
        }
        foreach(var(i,v) in shape.Fixes)
        {
            int y=v==1?side[i]:1-side[i],j=owner[i];
            if(fix[j]>=0&&fix[j]!=y)return No();
            fix[j]=y;
        }
        int[] index=new int[part.Count];int free=0;
        for(int j=0;j<part.Count;j++)index[j]=fix[j]>=0?-1:free++;
        trace.Mark("owner-fix");
        if(4L*free>3L*m.N)return new(shape,[],[],free,[],false,true);
        int[] map=new int[m.N],baseBit=new int[m.N];
        for(int i=0;i<m.N;i++)
        {
            int j=owner[i];map[i]=index[j];baseBit[i]=fix[j]>=0?(fix[j]==side[i]?1:0):1-side[i];
        }
        var rows=new List<Row>();
        foreach(var r in shape.Rows)
        {
            ct.ThrowIfCancellationRequested();
            long baseline=0;var coeff=new Dictionary<int,long>();
            foreach(int i in r.Scope)
            {
                baseline+=baseBit[i];
                if(map[i]>=0)coeff[map[i]]=coeff.GetValueOrDefault(map[i])+1-2*baseBit[i];
            }
            long lo=baseline+coeff.Values.Sum(v=>Math.Min(0,v)),hi=baseline+coeff.Values.Sum(v=>Math.Max(0,v));
            if(lo>r.U||hi<r.L)return No();
            if(lo>=r.L&&hi<=r.U)continue;
            var active=coeff.Where(x=>x.Value!=0).OrderBy(x=>x.Key).ToArray();
            rows.Add(new(active.Select(x=>x.Key).ToArray(),active.Select(x=>x.Value).ToArray(),r.L-baseline,r.U-baseline));
        }
        trace.Mark("row-transform");
        return new(shape,map,baseBit,free,rows.ToArray(),false,false);
    }
    internal WideResult Solve(WideModel m,Func<WideModel,WideResult> original,PhaseTrace trace,CancellationToken ct=default)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("single owner");
        try
        {
            ct.ThrowIfCancellationRequested();
            Admit(m);trace.Mark("plan-admission");
            TemplateHit=false;
            if(fast&&m.Edges.Length==0&&4L*m.Fixes.Length<m.N)
            {Hit=false;Route="original-fast";var direct=original(m);ct.ThrowIfCancellationRequested();return direct;}
            Hit=reuse&&plan!=null&&Same(plan.Shape,m);
            trace.Mark("identity");
            Plan p;
            if(Hit)p=plan!;
            else {p=Build(m,trace,ct);ct.ThrowIfCancellationRequested();if(reuse)plan=p;Builds++;templateModel=null;templateVariables=null;}
            trace.Mark("plan-ready");
            if(p.Impossible){Route="contradiction";return new("Infeasible",null,null,0,0);}
            if(p.Direct){Route="original";return original(m);}
            Route="quotient";
            long offset=0;long[] costs=new long[p.Free];
            for(int i=0;i<m.N;i++){long c=m.Cost[i];offset+=p.Base[i]*c;if(p.Map[i]>=0)costs[p.Map[i]]+=(1-2*p.Base[i])*c;}
            trace.Mark("objective");
            CpModel cp;BoolVar[] y;
            if(template&&Hit&&templateModel!=null&&templateVariables!=null)
            {
                cp=templateModel;y=templateVariables;TemplateHit=true;
            }
            else
            {
                cp=new CpModel();y=Enumerable.Range(0,p.Free).Select(i=>cp.NewBoolVar("y"+i)).ToArray();
                foreach(var r in p.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    cp.AddLinearConstraint(LinearExpr.WeightedSum(r.Index.Select(i=>y[i]),r.Coeff),r.L,r.U);
                }
                if(template){templateModel=cp;templateVariables=y;}
            }
            if(template)cp.ClearObjective();
            cp.Minimize(LinearExpr.WeightedSum(y,costs)+offset);trace.Mark("native-build");
            ct.ThrowIfCancellationRequested();
            var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};
            var status=solver.Solve(cp);trace.Mark("native-solve");ct.ThrowIfCancellationRequested();
            if(status is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(status.ToString(),null,null,p.Free,p.Rows.Length);
            var bits=new int[m.N];var values=y.Select(v=>checked((int)solver.Value(v))).ToArray();
            if(values.Any(v=>v is not(0 or 1)))throw new Exception("native domain");
            long value=0;
            for(int i=0;i<m.N;i++){bits[i]=p.Base[i]^(p.Map[i]<0?0:values[p.Map[i]]);value+=bits[i]*m.Cost[i];}
            trace.Mark("reconstruct");
            if(!double.IsFinite(solver.ObjectiveValue)||Math.Abs(solver.ObjectiveValue-value)>1e-6||
                status==CpSolverStatus.Optimal&&(!double.IsFinite(solver.BestObjectiveBound)||Math.Abs(solver.BestObjectiveBound-value)>1e-6))
                throw new Exception("native report mismatch");
            return new(status.ToString(),value,bits,p.Free,p.Rows.Length);
        }
        catch{if(template){templateModel=null;templateVariables=null;}throw;}
        finally{Volatile.Write(ref busy,0);}
    }
}

internal static class IncrementalStudy
{
    internal static void Run(string split,Func<WideModel,WideResult> original,Func<WideModel,WideResult> quotient,Func<WideModel,PhaseTrace,WideResult> adaptive,Func<WideModel,int[],long> verify)
    {
        bool dev=split=="dev";bool disk=split=="disk";if(!dev&&!disk&&split!="holdout")throw new ArgumentException("split");
        string[] arms=["original","G11","G12","cold","G13"];
        var options=new JsonSerializerOptions{IncludeFields=true};
        FileStream? persisted=disk?new FileStream("g13-disk-evidence.jsonl",FileMode.CreateNew):null;
        try
        {
        foreach(int n in dev?new[]{128,512}:disk?new[]{512,4096}:new[]{512,4096,10000,25000})
        foreach(string family in dev?new[]{"stable","edits"}:disk?new[]{"stable","edits"}:new[]{"stable","edits","weak","none"})
        for(int h=0;h<(dev||disk?1:3);h++)
        {
            int seed=(dev?1740101:disk?1800101:1770101)+h;var rng=new Random(seed+n);
            var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
            var edges=new List<(int A,int B)>();int covered=family=="none"?0:family=="weak"?n/5:n;
            int width=family=="weak"?2:7;
            for(int i=0;i<covered;i+=width)
                for(int j=i+1;j<Math.Min(i+width,covered);j++){edges.Add((i,j));planted[j]=1-planted[i];}
            var rows=new List<WideRow>();
            for(int i=0;i<n;i+=12){int[] scope=Enumerable.Range(i,Math.Min(12,n-i)).ToArray();int k=scope.Sum(j=>planted[j]);rows.Add(new(scope,k,k));}
            var fixes=new List<(int Bit,int Value)>();
            var cold=new IncrementalQuotient(false);var cache=new IncrementalQuotient(true);
            for(int rev=0;rev<5;rev++)
            {
                if(family=="edits"&&rev==2){edges.RemoveAt(0);fixes.Add((0,planted[0]));}
                if(family=="edits"&&rev==3){rows[0]=new(rows[0].Scope.Reverse().ToArray(),rows[0].L,rows[0].U);}
                if(family=="edits"&&rev==4){edges.Add((0,1));fixes.Clear();rows.RemoveAt(0);}
                var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-100,101)).ToArray());
                _=verify(m,planted);
                var results=new Dictionary<string,WideResult>();
                for(int a=0;a<arms.Length;a++)
                {
                    string arm=arms[(h+rev+a)%arms.Length];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();
                    var trace=new PhaseTrace();
                    IncrementalQuotient.Admit(m);trace.Mark("admission");
                    string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m,options)));trace.Mark("digest");
                    var r=arm switch{"original"=>original(m),"G11"=>quotient(m),"G12"=>adaptive(m,trace),"cold"=>cold.Solve(m,original,trace),_=>cache.Solve(m,original,trace)};
                    trace.Mark("solve-rest");
                    if(r.Status is "ModelInvalid" or "Invalid" or "Infeasible")throw new Exception("planted feasible model rejected");
                    if(r.Bits!=null&&verify(m,r.Bits)!=r.Value)throw new Exception("original witness");
                    trace.Mark("verify");
                    var bytes=JsonSerializer.SerializeToUtf8Bytes(new{digest,n,family,seed,revision=rev,arm,r.Status,r.Value,r.Bits},options);trace.Mark("encode");
                    if(persisted!=null){persisted.Write(bytes);persisted.WriteByte(10);persisted.Flush(true);trace.Mark("flush");}
                    long ticks=Stopwatch.GetTimestamp()-start;
                    results.Add(arm,r);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision=rev,arm,digest,status=r.Status,value=r.Value,verifiedWitness=r.Bits!=null,variables=r.Variables,rows=r.Rows,hit=arm=="G13"&&cache.Hit,route=arm=="G13"?cache.Route:arm=="cold"?cold.Route:arm,phases=trace.Ticks,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc},options));
                }
                foreach(var a in results.Values)foreach(var b in results.Values)
                {
                    if(a.Status=="Optimal"&&b.Status=="Optimal"&&a.Value!=b.Value)throw new Exception("optimal disagreement");
                    if(a.Status=="Optimal"&&b.Value<a.Value)throw new Exception("better than optimum");
                }
            }
        }
        }
        finally{persisted?.Dispose();}
    }
}
