using AlgorithmDiscoveryLab;
using Google.OrTools.Sat;
using System.Security.Cryptography;
using System.Text.Json;

internal record SignedRow(int[] Index,long[] Coeff,long L,long U);
internal record TransformCertificate(string Digest,int Free,int[] Map,int[] Base,SignedRow[] Rows,long Offset,long[] Cost);
internal static class CertifiedQuotient
{
    internal static readonly JsonSerializerOptions Json=new(){IncludeFields=true};
    internal static string Digest(WideModel m)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m,Json)));
    internal static TransformCertificate? Produce(WideModel m,string digest)
    {
        var parts=OrientationPartition.Build(m.N,m.Edges);if(parts==null)return null;
        int[] map=new int[m.N],b=new int[m.N],owner=new int[m.N],fixedChoice=new int[parts.Count];
        Array.Fill(fixedChoice,-1);
        for(int j=0;j<parts.Count;j++)
        {
            foreach(int i in parts[j].Zero){owner[i]=j;b[i]=1;}
            foreach(int i in parts[j].One){owner[i]=j;b[i]=0;}
        }
        foreach(var(i,v) in m.Fixes)
        {
            int y=b[i]^v,j=owner[i];
            if(fixedChoice[j]>=0&&fixedChoice[j]!=y)return null;
            fixedChoice[j]=y;
        }
        int free=0;int[] idx=new int[parts.Count];
        for(int j=0;j<parts.Count;j++)idx[j]=fixedChoice[j]<0?free++:-1;
        long off=0;long[] c=new long[free];
        for(int i=0;i<m.N;i++)
        {
            int j=owner[i];map[i]=idx[j];if(map[i]<0)b[i]^=fixedChoice[j];
            off+=b[i]*m.Cost[i];if(map[i]>=0)c[map[i]]+=(1-2*b[i])*m.Cost[i];
        }
        var rows=new List<SignedRow>();
        foreach(var r in m.Rows)
        {
            long k=0;var d=new SortedDictionary<int,long>();
            foreach(int i in r.Scope){k+=b[i];if(map[i]>=0)d[map[i]]=d.GetValueOrDefault(map[i])+1-2*b[i];}
            var a=d.Where(t=>t.Value!=0).ToArray();
            rows.Add(new(a.Select(t=>t.Key).ToArray(),a.Select(t=>t.Value).ToArray(),r.L-k,r.U-k));
        }
        return new(digest,free,map,b,rows.ToArray(),off,c);
    }
    // Exact signed unit-cardinality separability, including empty rows.
    internal static int[]? Direct(TransformCertificate t)
    {
        var used=new bool[t.Free];var y=t.Cost.Select(c=>c<0?1:0).ToArray();
        foreach(var r in t.Rows)
        {
            long lo=r.Coeff.Sum(v=>Math.Min(v,0)),hi=r.Coeff.Sum(v=>Math.Max(v,0));
            if(lo>=r.L&&hi<=r.U)continue;
            if(r.Coeff.Any(v=>v!=1&&v!=-1)||r.Index.Any(i=>used[i]))return null;
            int neg=r.Coeff.Count(v=>v<0);
            long lower=Math.Max(0,r.L+neg),upper=Math.Min(r.Index.Length,r.U+neg);
            if(lower>upper)return null;
            var a=r.Index.Select((i,j)=>(I:i,Sign:r.Coeff[j],Cost:r.Coeff[j]*t.Cost[i])).OrderBy(x=>x.Cost).ThenBy(x=>x.I).ToArray();
            int k=(int)Math.Clamp(a.Count(x=>x.Cost<0),lower,upper);
            for(int j=0;j<a.Length;j++){var x=a[j];used[x.I]=true;y[x.I]=(j<k?1:0)^(x.Sign<0?1:0);}
        }
        return y;
    }
    internal static WideResult SolveReduced(TransformCertificate t,bool direct, out string route,out int[]? reduced)
    {
        reduced=direct?Direct(t):null;
        if(reduced!=null){route="direct";return Reconstruct(t,reduced,"Optimal");}
        route="native-quotient";
        var cp=new CpModel();var y=Enumerable.Range(0,t.Free).Select(i=>cp.NewBoolVar("y"+i)).ToArray();
        foreach(var r in t.Rows)cp.AddLinearConstraint(LinearExpr.WeightedSum(r.Index.Select(i=>y[i]),r.Coeff),r.L,r.U);
        cp.Minimize(LinearExpr.WeightedSum(y,t.Cost)+t.Offset);
        var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};
        var status=solver.Solve(cp);
        if(status is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(status.ToString(),null,null,t.Free,t.Rows.Length);
        reduced=y.Select(v=>checked((int)solver.Value(v))).ToArray();var result=Reconstruct(t,reduced,status.ToString());
        if(!double.IsFinite(solver.ObjectiveValue)||Math.Abs(solver.ObjectiveValue-result.Value!.Value)>1e-6||
            status==CpSolverStatus.Optimal&&(!double.IsFinite(solver.BestObjectiveBound)||Math.Abs(solver.BestObjectiveBound-result.Value.Value)>1e-6))throw new Exception("native report");
        return result;
    }
    private static WideResult Reconstruct(TransformCertificate t,int[] y,string status)
    {
        int[] x=new int[t.Map.Length];long value=t.Offset;
        for(int j=0;j<y.Length;j++){if(y[j] is not(0 or 1))throw new Exception("domain");value+=y[j]*t.Cost[j];}
        for(int i=0;i<x.Length;i++)x[i]=t.Base[i]^(t.Map[i]<0?0:y[t.Map[i]]);
        return new(status,value,x,t.Free,t.Rows.Length);
    }
}

// Independent checker: connectivity via disjoint sets (not producer coloring), and
// exact row/objective reconstruction by sorted source contributions. Never trusts digest alone.
internal static class TransformationVerifier
{
    internal static bool Verify(WideModel m,string digest,TransformCertificate t)
    {
        if(t.Digest!=digest||t.Free<0||t.Free>m.N||t.Map.Length!=m.N||t.Base.Length!=m.N||t.Cost.Length!=t.Free||t.Rows.Length!=m.Rows.Length)return false;
        if(t.Map.Any(v=>v< -1||v>=t.Free)||t.Base.Any(v=>v is not(0 or 1)))return false;
        int[] parent=Enumerable.Range(0,m.N).ToArray();int[] size=Enumerable.Repeat(1,m.N).ToArray();
        int Root(int i){while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i;}
        foreach(var(a,b) in m.Edges)
        {
            if(t.Map[a]!=t.Map[b]||t.Base[a]+t.Base[b]!=1)return false;
            int u=Root(a),v=Root(b);if(u!=v){if(size[u]<size[v])(u,v)=(v,u);parent[v]=u;size[u]+=size[v];}
        }
        var anchored=new bool[m.N];
        foreach(var(i,v) in m.Fixes){if(t.Map[i]!=-1||t.Base[i]!=v)return false;anchored[Root(i)]=true;}
        int[] owners=Enumerable.Repeat(-1,t.Free).ToArray();
        for(int i=0;i<m.N;i++)
        {
            int j=t.Map[i],root=Root(i);
            if(j<0){if(!anchored[root])return false;continue;}
            if(anchored[root])return false;
            if(owners[j]>=0&&owners[j]!=root)return false;owners[j]=root;
        }
        if(owners.Any(v=>v<0))return false;
        // Adjacent same map and connectedness guarantee a whole component shares its map.
        Int128 offset=0;var costs=new Int128[t.Free];
        for(int i=0;i<m.N;i++){offset+=(Int128)m.Cost[i]*t.Base[i];if(t.Map[i]>=0)costs[t.Map[i]]+=(Int128)m.Cost[i]*(t.Base[i]==0?1:-1);}
        if(offset!=t.Offset||costs.Where((v,j)=>v!=t.Cost[j]).Any())return false;
        for(int j=0;j<m.Rows.Length;j++)
        {
            var r=m.Rows[j];var q=t.Rows[j];long k=r.Scope.Sum(i=>(long)t.Base[i]);
            if(q.L!=r.L-k||q.U!=r.U-k||q.Index.Length!=q.Coeff.Length)return false;
            var expected=r.Scope.Where(i=>t.Map[i]>=0).GroupBy(i=>t.Map[i]).Select(g=>(I:g.Key,D:g.Sum(i=>t.Base[i]==0?1L:-1L))).Where(a=>a.D!=0).OrderBy(a=>a.I).ToArray();
            if(expected.Length!=q.Index.Length)return false;
            for(int i=0;i<expected.Length;i++)if(expected[i].I!=q.Index[i]||expected[i].D!=q.Coeff[i])return false;
        }
        return true;
    }
    // Exchange conditions are sufficient for the disjoint signed unit-cardinality optimum.
    internal static bool VerifyDirect(TransformCertificate t,int[] y)
    {
        if(y.Length!=t.Free||y.Any(v=>v is not(0 or 1)))return false;
        var used=new bool[t.Free];
        foreach(var r in t.Rows)
        {
            long lo=0,hi=0,total=0;
            for(int i=0;i<r.Index.Length;i++){long a=r.Coeff[i];lo+=Math.Min(a,0);hi+=Math.Max(a,0);total+=a*y[r.Index[i]];}
            if(total<r.L||total>r.U)return false;
            if(lo>=r.L&&hi<=r.U)continue;
            long maxSelected=long.MinValue,minUnselected=long.MaxValue;int count=0,neg=0;
            for(int j=0;j<r.Index.Length;j++)
            {
                int i=r.Index[j];long a=r.Coeff[j];if(used[i]||a is not(1 or -1))return false;used[i]=true;
                if(a<0)neg++;int z=y[i]^(a<0?1:0);long c=a*t.Cost[i];
                if(z==1){count++;maxSelected=Math.Max(maxSelected,c);}else minUnselected=Math.Min(minUnselected,c);
            }
            long lower=Math.Max(0,r.L+neg),upper=Math.Min(r.Index.Length,r.U+neg);
            if(count<lower||count>upper||maxSelected>minUnselected||count>lower&&maxSelected>0||count<upper&&minUnselected<0)return false;
        }
        for(int i=0;i<y.Length;i++)if(!used[i]&&((y[i]==1&&t.Cost[i]>0)||(y[i]==0&&t.Cost[i]<0)))return false;
        return true;
    }
}
