using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;

internal static class IncrementalAudit
{
    private static readonly JsonSerializerOptions Options=new(){IncludeFields=true};
    private static long Evaluate(WideModel m,int[] bits)
    {
        if(bits.Length!=m.N||bits.Any(b=>b!=0&&b!=1))throw new Exception("binary domain");
        foreach(var e in m.Edges)if(bits[e.A]+bits[e.B]!=1)throw new Exception("edge");
        foreach(var f in m.Fixes)if(bits[f.Bit]!=f.Value)throw new Exception("fixed");
        foreach(var r in m.Rows){int s=0;foreach(int i in r.Scope)s+=bits[i];if(s<r.L||s>r.U)throw new Exception("row");}
        long c=0;for(int i=0;i<m.N;i++)if(bits[i]==1)c+=m.Cost[i];return c;
    }
    private static long? Exact(WideModel m)
    {
        long? optimum=null;
        for(int mask=0;mask<(1<<m.N);mask++)
        {
            int X(int i)=>(mask>>i)&1;
            if(m.Edges.Any(e=>X(e.A)+X(e.B)!=1)||m.Fixes.Any(f=>X(f.Bit)!=f.Value))continue;
            if(m.Rows.Any(r=>{int c=r.Scope.Sum(X);return c<r.L||c>r.U;}))continue;
            long value=Enumerable.Range(0,m.N).Sum(i=>m.Cost[i]*X(i));
            if(optimum==null||value<optimum)optimum=value;
        }
        return optimum;
    }
    private static void Match(WideModel m,WideResult r,long? expected)
    {
        if(r.Status!=(expected.HasValue?"Optimal":"Infeasible")||r.Value!=expected||
            (expected.HasValue&&(r.Bits==null||Evaluate(m,r.Bits)!=expected)))throw new Exception("reference mismatch");
    }
    internal static void Run(Func<WideModel,WideResult> original)
    {
        var cache=new IncrementalQuotient(true);
        int checks=0;
        for(int s=0;s<64;s++)
        {
            var rng=new Random(1860101+s);int n=8;
            var edges=new List<(int,int)>();for(int i=0;i<7;i++)if(rng.Next(3)!=0)edges.Add((i,i+1));
            var rows=new List<WideRow>{new([0,2,4,6],1,3),new([1,3,5,7],1,2)};
            var fixes=new List<(int,int)>();
            var cost=Enumerable.Range(0,n).Select(_=>(long)rng.Next(-20,21)).ToArray();
            for(int rev=0;rev<8;rev++)
            {
                if(rev==1)cost[0]+=13;
                if(rev==2)edges.Add((0,2));
                if(rev==3)edges.RemoveAt(edges.Count-1);
                if(rev==4)fixes.Add((0,0));
                if(rev==5)rows[0]=new([6,4,2,0],2,2);
                if(rev==6)fixes.Add((0,1));
                if(rev==7){fixes.Clear();edges.Reverse();rows.Reverse();}
                var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),cost.ToArray());
                var r=cache.Solve(m,original,new());
                long? expected=Exact(m);
                Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",seed=s,rev,model=m,status=r.Status,value=r.Value,expected,bits=r.Bits,hit=cache.Hit},Options));
                Match(m,r,expected);checks++;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="finite-summary",checks}));
        void Assert(string name,bool ok){Console.WriteLine(JsonSerializer.Serialize(new{kind="boundary",name,valid=ok}));if(!ok)throw new Exception(name);}
        bool Reject<T>(Action a)where T:Exception{try{a();return false;}catch(T){return true;}}
        var local=new IncrementalQuotient(true);
        var m0=new WideModel(4,[(0,1),(1,2),(2,3)],[],[new([0,2],0,2)],[3,-4,5,-6]);
        Match(m0,local.Solve(m0,original,new()),Exact(m0));
        m0.Cost[0]=-30;Match(m0,local.Solve(m0,original,new()),Exact(m0));
        Assert("fresh-objective-hit",local.Hit);
        m0.Rows[0].Scope[0]=1;Match(m0,local.Solve(m0,original,new()),Exact(m0));
        Assert("same-array-row-invalidates",!local.Hit);
        m0.Edges[0]=(0,2);Match(m0,local.Solve(m0,original,new()),Exact(m0));
        Assert("same-array-edge-invalidates",!local.Hit);
        m0.Edges[0]=(0,1);Match(m0,local.Solve(m0,original,new()),Exact(m0));
        Assert("restore-rebuild",!local.Hit);
        Assert("pre-cancel",Reject<OperationCanceledException>(()=>local.Solve(m0,original,new(),new CancellationToken(true))));
        Assert("invalid-endpoint",Reject<ArgumentException>(()=>local.Solve(m0 with{Edges=[(0,4)]},original,new())));
        Assert("duplicate-scope",Reject<ArgumentException>(()=>local.Solve(m0 with{Rows=[new([0,0],0,2)]},original,new())));
        Assert("invalid-domain",Reject<ArgumentException>(()=>local.Solve(m0 with{Fixes=[(1,2)]},original,new())));
        Assert("overflow",Reject<OverflowException>(()=>local.Solve(m0 with{Cost=[long.MaxValue,0,0,0]},original,new())));
        Match(m0,local.Solve(m0,original,new()),Exact(m0));Assert("recovery-preserves-plan",local.Hit);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var uncompressed=new WideModel(4,[],[],[],[1,2,3,4]);
        var t=Task.Run(()=>local.Solve(uncompressed,m=>{entered.Set();release.Wait();return original(m);},new()));
        if(!entered.Wait(TimeSpan.FromSeconds(5)))throw new Exception("concurrency setup");
        try{Assert("same-session-overlap-rejected",Reject<InvalidOperationException>(()=>local.Solve(m0,original,new())));}
        finally{release.Set();}Match(uncompressed,t.GetAwaiter().GetResult(),0);
        Assert("separate-session-isolation",Enumerable.Range(0,4).AsParallel().All(i=>{var m=uncompressed with{Cost=[-i,2,3,4]};return new IncrementalQuotient(true).Solve(m,original,new()).Value==-i;}));
        foreach(int n in new[]{64,512,4096})
        foreach(string family in new[]{"structured","uncompressed","contradiction"})
        for(int seed=0;seed<2;seed++)
        {
            var rng=new Random(1890101+seed+n);var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
            var edges=new List<(int,int)>();
            if(family!="uncompressed")for(int i=0;i<n-1;i+=2){edges.Add((i,i+1));planted[i+1]=1-planted[i];}
            var rows=new List<WideRow>();
            for(int i=0;i<n;i+=8){int[] scope=Enumerable.Range(i,Math.Min(8,n-i)).ToArray();int v=scope.Sum(j=>planted[j]);rows.Add(new(scope,v,v));}
            var fixes=new List<(int,int)>();
            var session=new IncrementalQuotient(true);
            for(int rev=0;rev<3;rev++)
            {
                if(rev==2){if(family=="contradiction"){fixes.Add((0,0));fixes.Add((1,0));}else fixes.Add((0,planted[0]));}
                var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-25,26)).ToArray());
                var a=session.Solve(m,original,new());var b=Highs(m);
                bool ca=a.Status is "Optimal" or "Infeasible",cb=b.Status is "Optimal" or "Infeasible";
                if(a.Bits!=null&&Evaluate(m,a.Bits)!=a.Value||b.Bits!=null&&Evaluate(m,b.Bits)!=b.Value)throw new Exception("wide witness");
                Console.WriteLine(JsonSerializer.Serialize(new{kind="highs",n,family,seed,rev,model=m,g13=a,highs=b,conclusive=ca&&cb,hit=session.Hit},Options));
                if(ca&&cb&&(a.Status!=b.Status||a.Value!=b.Value))throw new Exception("independent solver mismatch");
                if(a.Status=="Optimal"&&b.Value<a.Value||b.Status=="Optimal"&&a.Value<b.Value)throw new Exception("bound mismatch");
                if(a.Status=="Infeasible"&&b.Bits!=null||b.Status=="Infeasible"&&a.Bits!=null)throw new Exception("status mismatch");
            }
        }
    }
    private static WideResult Highs(WideModel m)
    {
        var start=new List<int>();var index=new List<int>();var val=new List<double>();var lower=new List<double>();var upper=new List<double>();
        void Row(int[] scope,int lo,int hi){start.Add(index.Count);index.AddRange(scope);val.AddRange(scope.Select(_=>1d));lower.Add(lo);upper.Add(hi);}
        foreach(var e in m.Edges){if(e.A==e.B)throw new Exception("unsupported audit duplicate column");Row([e.A,e.B],1,1);}
        foreach(var f in m.Fixes)Row([f.Bit],f.Value,f.Value);
        foreach(var r in m.Rows)Row(r.Scope,r.L,r.U);
        IntPtr h=Native.Highs_create();if(h==IntPtr.Zero)throw new Exception("HiGHS create");
        void Ok(int status){if(status<0)throw new Exception("HiGHS native error");}
        try
        {
            Ok(Native.Highs_setBoolOptionValue(h,"output_flag",0));
            Ok(Native.Highs_setIntOptionValue(h,"threads",1));
            Ok(Native.Highs_setDoubleOptionValue(h,"time_limit",2));
            Ok(Native.Highs_setDoubleOptionValue(h,"mip_rel_gap",0));
            Ok(Native.Highs_setDoubleOptionValue(h,"mip_abs_gap",0));
            Ok(Native.Highs_passMip(h,m.N,start.Count,index.Count,2,1,0,m.Cost.Select(c=>(double)c).ToArray(),new double[m.N],Enumerable.Repeat(1d,m.N).ToArray(),lower.ToArray(),upper.ToArray(),start.ToArray(),index.ToArray(),val.ToArray(),Enumerable.Repeat(1,m.N).ToArray()));
            Ok(Native.Highs_run(h));int status=Native.Highs_getModelStatus(h);
            if(status==8)return new("Infeasible",null,null,m.N,start.Count);
            if(status!=7)return new("Unknown:"+status,null,null,m.N,start.Count);
            var x=new double[m.N];Ok(Native.Highs_getSolution(h,x,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero));
            int[] bits=x.Select(v=>{if(!double.IsFinite(v)||Math.Abs(v-Math.Round(v))>1e-6||v< -1e-6||v>1+1e-6)throw new Exception("HiGHS integrality");return (int)Math.Round(v);}).ToArray();
            long value=Evaluate(m,bits);Ok(Native.Highs_getDoubleInfoValue(h,"objective_function_value",out double objective));
            if(!double.IsFinite(objective)||Math.Abs(objective-value)>1e-6)throw new Exception("HiGHS objective");
            return new("Optimal",value,bits,m.N,start.Count);
        }
        finally{Native.Highs_destroy(h);}
    }
    private static class Native
    {
        const string Lib="highs.so";
        [DllImport(Lib)]internal static extern IntPtr Highs_create();
        [DllImport(Lib)]internal static extern void Highs_destroy(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_setBoolOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setIntOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setDoubleOptionValue(IntPtr h,string name,double value);
        [DllImport(Lib)]internal static extern int Highs_passMip(IntPtr h,int n,int m,int nnz,int format,int sense,double offset,double[] cost,double[] cl,double[] cu,double[] rl,double[] ru,int[] start,int[] idx,double[] vals,int[] integer);
        [DllImport(Lib)]internal static extern int Highs_run(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getModelStatus(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getSolution(IntPtr h,[Out] double[] x,IntPtr cd,IntPtr rv,IntPtr rd);
        [DllImport(Lib)]internal static extern int Highs_getDoubleInfoValue(IntPtr h,string name,out double value);
    }
}
