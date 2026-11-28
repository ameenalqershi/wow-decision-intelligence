using Google.OrTools.Graph;
using Google.OrTools.Sat;
using System.Text.Json;
internal record GeneralArc(int From,int To,long Lower,long Upper,long Cost);
internal record GeneralNetwork(int N,long[] Supply,GeneralArc[] Arcs);
internal record NetworkDecision(string Status,long? Value,long[]? Flow,long[]? Potential,int[]? Cut);
internal static class GeneralFlow
{
    internal static string Digest(GeneralNetwork n)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(n,CertifiedQuotient.Json)));
    internal static void Admit(GeneralNetwork n)
    {
        if(n.N<2||n.N>1024||n.Supply.Length!=n.N||n.Arcs.Length>100000||(long)n.N*n.Arcs.Length>8000000)throw new ArgumentException("network envelope");
        if(n.Supply.Aggregate((Int128)0,(s,v)=>s+v)!=0)throw new ArgumentException("unbalanced input");
        if(n.Supply.Aggregate((Int128)0,(s,v)=>s+Int128.Abs(v))>1L<<40)throw new OverflowException("supply");
        Int128 cost=0,capacity=0;
        foreach(var a in n.Arcs){
            if(a.From<0||a.From>=n.N||a.To<0||a.To>=n.N||a.From==a.To||a.Lower<0||a.Upper<a.Lower)throw new ArgumentException("arc");
            capacity+=a.Upper;cost+=(Int128)a.Upper*Int128.Abs(a.Cost);
            if(Int128.Abs(a.Cost)>1L<<40||capacity>1L<<40||cost>1L<<50)throw new OverflowException("envelope");
        }
    }
    private static long[] Shift(GeneralNetwork n){
        var b=n.Supply.ToArray();foreach(var a in n.Arcs){b[a.From]-=a.Lower;b[a.To]+=a.Lower;}return b;
    }
    internal static NetworkDecision Solve(GeneralNetwork n,bool certified)
    {
        var b=Shift(n);using var solver=new MinCostFlow();
        foreach(var a in n.Arcs)solver.AddArcWithCapacityAndUnitCost(a.From,a.To,a.Upper-a.Lower,a.Cost);
        for(int i=0;i<n.N;i++)solver.SetNodeSupply(i,b[i]);
        var s=solver.Solve();
        if(s==MinCostFlow.Status.INFEASIBLE){
            int[]? cut=certified?FindCut(n,b):null;
            if(certified&&cut==null)throw new Exception("missing cut");
            return new("Infeasible",null,null,null,cut);
        }
        if(s!=MinCostFlow.Status.OPTIMAL)return new("Unknown",null,null,null,null);
        long[] x=n.Arcs.Select((a,i)=>a.Lower+solver.Flow(i)).ToArray();
        long cost=n.Arcs.Select((a,i)=>a.Cost*x[i]).Sum();
        if(cost!=solver.OptimalCost()+n.Arcs.Sum(a=>a.Cost*a.Lower))throw new Exception("native cost");
        long[]? p=null;
        if(certified){
            var dist=new Int128[n.N];
            for(int k=0;k<n.N;k++){
                bool changed=false;
                void Relax(int a,int b,long c){var d=dist[a]+c;if(d<dist[b]){dist[b]=d;changed=true;}}
                for(int i=0;i<x.Length;i++){var a=n.Arcs[i];if(x[i]<a.Upper)Relax(a.From,a.To,a.Cost);if(x[i]>a.Lower)Relax(a.To,a.From,-a.Cost);}
                if(!changed)break;if(k==n.N-1)throw new Exception("negative residual cycle");
            }
            p=dist.Select(v=>checked((long)v)).ToArray();
        }
        return new("Optimal",cost,x,p,null);
    }
    private static int[]? FindCut(GeneralNetwork n,long[] b)
    {
        // Bounded residual BFS max-flow, separate from min-cost engine. Only on reported infeasibility.
        int s=n.N,t=s+1;var adj=Enumerable.Range(0,n.N+2).Select(_=>new List<int>()).ToArray();
        var to=new List<int>();var cap=new List<long>();
        void Add(int a,int z,long c){int i=to.Count;adj[a].Add(i);to.Add(z);cap.Add(c);adj[z].Add(i+1);to.Add(a);cap.Add(0);}
        foreach(var a in n.Arcs)Add(a.From,a.To,a.Upper-a.Lower);
        for(int i=0;i<n.N;i++)if(b[i]>0)Add(s,i,b[i]);else if(b[i]<0)Add(i,t,-b[i]);
        for(int pass=0;pass<100000;pass++){
            int[] prev=Enumerable.Repeat(-1,n.N+2).ToArray();prev[s]=-2;var q=new Queue<int>();q.Enqueue(s);
            while(q.TryDequeue(out int a)&&prev[t]==-1)foreach(int i in adj[a])if(cap[i]>0&&prev[to[i]]==-1){prev[to[i]]=i;q.Enqueue(to[i]);}
            if(prev[t]==-1){var cut=Enumerable.Range(0,n.N).Where(i=>prev[i]!=-1).ToArray();return NetworkVerifier.Cut(n,cut)?cut:null;}
            long f=long.MaxValue;for(int v=t;v!=s;v=to[prev[v]^1])f=Math.Min(f,cap[prev[v]]);
            for(int v=t;v!=s;v=to[prev[v]^1]){int i=prev[v];cap[i]-=f;cap[i^1]+=f;}
        }
        return null;
    }
    internal static NetworkDecision Original(GeneralNetwork n)
    {
        var cp=new CpModel();var x=n.Arcs.Select((a,i)=>cp.NewIntVar(a.Lower,a.Upper,"x"+i)).ToArray();
        var lists=Enumerable.Range(0,n.N).Select(_=>new List<(IntVar X,long Coeff)>()).ToArray();
        for(int i=0;i<x.Length;i++){var a=n.Arcs[i];lists[a.From].Add((x[i],1));lists[a.To].Add((x[i],-1));}
        for(int i=0;i<n.N;i++)cp.Add(LinearExpr.WeightedSum(lists[i].Select(v=>v.X),lists[i].Select(v=>v.Coeff))==n.Supply[i]);
        cp.Minimize(LinearExpr.WeightedSum(x,n.Arcs.Select(a=>a.Cost)));
        var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};
        var st=solver.Solve(cp);if(st is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(st.ToString(),null,null,null,null);
        var f=x.Select(solver.Value).ToArray();long value=n.Arcs.Select((a,i)=>a.Cost*f[i]).Sum();
        if(Math.Abs(solver.ObjectiveValue-value)>1e-6||st==CpSolverStatus.Optimal&&Math.Abs(solver.BestObjectiveBound-value)>1e-6)throw new Exception("CP objective");
        return new(st.ToString(),value,f,null,null);
    }
}
internal static class NetworkVerifier
{
    internal static bool Cut(GeneralNetwork n,int[] nodes)
    {
        if(nodes.Length==0||nodes.Any(i=>i<0||i>=n.N)||nodes.Distinct().Count()!=nodes.Length)return false;
        var set=nodes.ToHashSet();Int128 b=0,bound=0;
        foreach(int i in nodes)b+=n.Supply[i];
        foreach(var a in n.Arcs){bool from=set.Contains(a.From),to=set.Contains(a.To);if(from&&!to)bound+=a.Upper;else if(!from&&to)bound-=a.Lower;}
        return b>bound;
    }
    internal static bool Verify(GeneralNetwork n,NetworkDecision r,bool requireProof)
    {
        if(r.Status=="Infeasible")return r.Flow==null&&r.Value==null&&r.Potential==null&&(r.Cut==null?!requireProof:Cut(n,r.Cut));
        if(r.Status=="Unknown")return r.Flow==null&&r.Value==null&&r.Potential==null&&r.Cut==null&&!requireProof;
        if(r.Status is not("Optimal" or "Feasible")||r.Flow==null||r.Flow.Length!=n.Arcs.Length||r.Value==null||r.Cut!=null)return false;
        var balance=new Int128[n.N];Int128 cost=0;
        if(requireProof&&(r.Potential==null||r.Status!="Optimal"))return false;
        if(r.Potential!=null&&(r.Potential.Length!=n.N||r.Status!="Optimal"))return false;
        for(int i=0;i<n.Arcs.Length;i++){
            var a=n.Arcs[i];long x=r.Flow[i];if(x<a.Lower||x>a.Upper)return false;
            balance[a.From]+=x;balance[a.To]-=x;cost+=(Int128)a.Cost*x;
            if(r.Potential!=null){Int128 rc=(Int128)a.Cost+r.Potential[a.From]-r.Potential[a.To];if(x<a.Upper&&rc<0||x>a.Lower&&rc>0)return false;}
        }
        return cost==r.Value&&balance.Where((v,i)=>v!=n.Supply[i]).Count()==0;
    }
}
