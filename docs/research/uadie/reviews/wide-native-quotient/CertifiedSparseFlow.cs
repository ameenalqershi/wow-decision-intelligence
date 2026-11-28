using Google.OrTools.Graph;
internal record FlowProof(string Digest,int[] Left,int[] Right,int[] From,int[] To,long[] Potential);
internal static class CertifiedSparseFlow
{
    private record Arc(int A,int B,long Cost,int Capacity,int Flow);
    private static (int[] Left,int[] Right,int[] From,int[] To)? Discover(WideModel m)
    {
        if(m.Edges.Length!=0||m.Fixes.Length!=0||m.Rows.Length<2||m.Rows.Length>1024||m.Rows.Any(r=>r.U!=1||r.L is not(0 or 1)||r.Scope.Length==0))return null;
        int[] first=Enumerable.Repeat(-1,m.N).ToArray(),second=Enumerable.Repeat(-1,m.N).ToArray();
        for(int j=0;j<m.Rows.Length;j++)foreach(int i in m.Rows[j].Scope){if(first[i]<0)first[i]=j;else if(second[i]<0)second[i]=j;else return null;}
        if(first.Any(i=>i<0)||second.Any(i=>i<0))return null;
        int[] color=Enumerable.Repeat(-1,m.Rows.Length).ToArray();
        for(int start=0;start<color.Length;start++)if(color[start]<0){
            var q=new Queue<int>();var component=new List<int>();color[start]=0;q.Enqueue(start);
            while(q.TryDequeue(out int r)){
                component.Add(r);
                foreach(int i in m.Rows[r].Scope){int other=first[i]==r?second[i]:first[i];if(color[other]==color[r])return null;if(color[other]<0){color[other]=1-color[r];q.Enqueue(other);}}
            }
            var optional=component.Where(r=>m.Rows[r].L==0).ToArray();
            if(optional.Length>0){
                int c=color[optional[0]];if(optional.Any(r=>color[r]!=c))return null;
                if(c==0)foreach(int r in component)color[r]^=1;
            }
        }
        int[] left=Enumerable.Range(0,color.Length).Where(i=>color[i]==0).ToArray(),right=Enumerable.Range(0,color.Length).Where(i=>color[i]==1).ToArray();
        if(left.Length==0||right.Length==0||left.Any(i=>m.Rows[i].L!=1)||left.Length>right.Length)return null;
        bool exact=right.All(i=>m.Rows[i].L==1),optionalRight=right.All(i=>m.Rows[i].L==0);
        if(!exact&&!optionalRight||exact&&left.Length!=right.Length)return null;
        int[] from=new int[m.N],to=new int[m.N];
        for(int i=0;i<m.N;i++){int a=first[i],b=second[i];if(color[a]!=0)(a,b)=(b,a);from[i]=a;to[i]=b;}
        return(left,right,from,to);
    }
    internal static (FlowProof Proof,WideResult Result)? Solve(WideModel m,string digest)
    {
        var s=Discover(m);if(s==null)return null;var(left,right,from,to)=s.Value;int sink=m.Rows.Length;
        using var solver=new MinCostFlow();
        for(int i=0;i<m.N;i++)solver.AddArcWithCapacityAndUnitCost(from[i],to[i],1,m.Cost[i]);
        foreach(int j in right)solver.AddArcWithCapacityAndUnitCost(j,sink,1,0);
        foreach(int i in left)solver.SetNodeSupply(i,1);
        solver.SetNodeSupply(sink,-left.Length);
        if(solver.Solve()!=MinCostFlow.Status.OPTIMAL)return null;
        int[] bits=Enumerable.Range(0,m.N).Select(i=>checked((int)solver.Flow(i))).ToArray();
        long value=CertifiedStudy.Verify(m,bits);if(value!=solver.OptimalCost())throw new Exception("flow report");
        var arcs=Arcs(m,right,from,to,bits);
        var dist=new Int128[sink+1];
        // Find feasible residual potentials by Bellman-Ford from virtual zero source.
        for(int pass=0;pass<dist.Length;pass++){
            bool changed=false;
            void Relax(int a,int b,long c){Int128 d=dist[a]+c;if(d<dist[b]){dist[b]=d;changed=true;}}
            foreach(var a in arcs){if(a.Flow<a.Capacity)Relax(a.A,a.B,a.Cost);if(a.Flow>0)Relax(a.B,a.A,-a.Cost);}
            if(!changed)break;if(pass==dist.Length-1)throw new Exception("negative residual cycle");
        }
        var proof=new FlowProof(digest,left,right,from,to,dist.Select(x=>checked((long)x)).ToArray());
        var r=new WideResult("Optimal",value,bits,m.N,m.Rows.Length);
        if(!Verify(m,digest,proof,r))throw new Exception("flow proof");
        return(proof,r);
    }
    private static Arc[] Arcs(WideModel m,int[] right,int[] from,int[] to,int[] bits)
    {
        var a=new List<Arc>();var load=new int[m.Rows.Length];
        for(int i=0;i<m.N;i++){a.Add(new(from[i],to[i],m.Cost[i],1,bits[i]));load[to[i]]+=bits[i];}
        foreach(int j in right)a.Add(new(j,m.Rows.Length,0,1,load[j]));
        return a.ToArray();
    }
    internal static bool Verify(WideModel m,string d,FlowProof p,WideResult r)
    {
        if(p.Digest!=d||m.Edges.Length!=0||m.Fixes.Length!=0||p.From.Length!=m.N||p.To.Length!=m.N||p.Potential.Length!=m.Rows.Length+1||
            p.Left.Length==0||p.Right.Length==0||!p.Left.Concat(p.Right).Order().SequenceEqual(Enumerable.Range(0,m.Rows.Length)))return false;
        if(p.Left.Any(j=>m.Rows[j].L!=1||m.Rows[j].U!=1)||p.Right.Any(j=>m.Rows[j].U!=1))return false;
        bool exact=p.Right.All(j=>m.Rows[j].L==1),optional=p.Right.All(j=>m.Rows[j].L==0);
        if(!exact&&!optional||exact&&p.Left.Length!=p.Right.Length)return false;
        var ls=p.Left.ToHashSet();var rs=p.Right.ToHashSet();
        var scopes=Enumerable.Range(0,m.Rows.Length).Select(_=>new List<int>()).ToArray();
        for(int i=0;i<m.N;i++){if(!ls.Contains(p.From[i])||!rs.Contains(p.To[i]))return false;scopes[p.From[i]].Add(i);scopes[p.To[i]].Add(i);}
        for(int j=0;j<m.Rows.Length;j++)if(!scopes[j].Order().SequenceEqual(m.Rows[j].Scope.Order()))return false;
        if(r.Status!="Optimal"||r.Bits==null||r.Bits.Length!=m.N||r.Bits.Any(x=>x is not(0 or 1)))return false;
        try{if(CertifiedStudy.Verify(m,r.Bits)!=r.Value)return false;}catch{ return false; }
        // Check each source arc and independently derive each right-to-sink flow.
        bool Residual(int a,int b,long c,int x){
            Int128 rc=(Int128)c+p.Potential[a]-p.Potential[b];
            return x is 0 or 1&&(x==1||rc>=0)&&(x==0||rc<=0);
        }
        for(int i=0;i<m.N;i++)if(!Residual(p.From[i],p.To[i],m.Cost[i],r.Bits[i]))return false;
        foreach(int j in p.Right)if(!Residual(j,m.Rows.Length,0,m.Rows[j].Scope.Sum(i=>r.Bits[i])))return false;
        return true;
    }
}
