using Google.OrTools.Graph;

// Strict source grammar: complete square bipartite assignment, each source
// variable an edge in two exact-one rows. No side constraints or fixed bits.
internal record AssignmentShape(int K,int[] LeftRows,int[] RightRows,int[] Matrix);
internal record AssignmentProof(string Digest,AssignmentShape Shape,int[] Match,long[] U,long[] V);
internal static class CertifiedAssignment
{
    internal static AssignmentShape? Discover(WideModel m)
    {
        int k=(int)Math.Sqrt(m.N);
        if(k<3||k>316||k*k!=m.N||m.Edges.Length!=0||m.Fixes.Length!=0||m.Rows.Length!=2*k||m.Rows.Any(r=>r.L!=1||r.U!=1||r.Scope.Length!=k))return null;
        int[] first=Enumerable.Repeat(-1,m.N).ToArray(),second=Enumerable.Repeat(-1,m.N).ToArray();
        for(int r=0;r<m.Rows.Length;r++)foreach(int i in m.Rows[r].Scope){
            if(first[i]<0)first[i]=r;else if(second[i]<0&&first[i]!=r)second[i]=r;else return null;
        }
        if(first.Any(i=>i<0)||second.Any(i=>i<0))return null;
        int[] color=Enumerable.Repeat(-1,2*k).ToArray();color[0]=0;var queue=new Queue<int>();queue.Enqueue(0);
        while(queue.TryDequeue(out int r))foreach(int i in m.Rows[r].Scope){
            int other=first[i]==r?second[i]:first[i];
            if(color[other]==color[r])return null;
            if(color[other]<0){color[other]=1-color[r];queue.Enqueue(other);}
        }
        var left=Enumerable.Range(0,2*k).Where(i=>color[i]==0).ToArray();
        var right=Enumerable.Range(0,2*k).Where(i=>color[i]==1).ToArray();
        if(left.Length!=k||right.Length!=k)return null;
        int[] local=new int[2*k];for(int i=0;i<k;i++){local[left[i]]=i;local[right[i]]=i;}
        int[] matrix=Enumerable.Repeat(-1,m.N).ToArray();
        for(int i=0;i<m.N;i++){
            int a=first[i],b=second[i];if(color[a]!=0)(a,b)=(b,a);
            int pos=local[a]*k+local[b];if(matrix[pos]>=0)return null;matrix[pos]=i;
        }
        if(matrix.Any(i=>i<0))return null;
        return new(k,left,right,matrix);
    }
    internal static AssignmentProof Hungarian(WideModel m,AssignmentShape s,string digest)
    {
        int k=s.K;var u=new Int128[k+1];var v=new Int128[k+1];var p=new int[k+1];var way=new int[k+1];
        for(int i=1;i<=k;i++)
        {
            p[0]=i;int j0=0;var min=Enumerable.Repeat(Int128.MaxValue/4,k+1).ToArray();var used=new bool[k+1];
            do{
                used[j0]=true;int i0=p[j0],j1=0;Int128 delta=Int128.MaxValue/4;
                for(int j=1;j<=k;j++)if(!used[j]){
                    Int128 current=(Int128)m.Cost[s.Matrix[(i0-1)*k+j-1]]-u[i0]-v[j];
                    if(current<min[j]){min[j]=current;way[j]=j0;}
                    if(min[j]<delta){delta=min[j];j1=j;}
                }
                if(j1==0)throw new Exception("no augmenting column");
                for(int j=0;j<=k;j++)if(used[j]){u[p[j]]+=delta;v[j]-=delta;}else min[j]-=delta;
                j0=j1;
            }while(p[j0]!=0);
            do{int j1=way[j0];p[j0]=p[j1];j0=j1;}while(j0!=0);
        }
        int[] match=new int[k];for(int j=1;j<=k;j++)match[p[j]-1]=j-1;
        return new(digest,s,match,u.Skip(1).Select(x=>checked((long)x)).ToArray(),v.Skip(1).Select(x=>checked((long)x)).ToArray());
    }
    internal static int[] Native(WideModel m,AssignmentShape s)
    {
        using var solver=new LinearSumAssignment();int k=s.K;
        for(int i=0;i<k;i++)for(int j=0;j<k;j++)solver.AddArcWithCost(i,j,m.Cost[s.Matrix[i*k+j]]);
        if(solver.Solve()!=LinearSumAssignment.Status.OPTIMAL)throw new Exception("native assignment did not prove optimal");
        var match=Enumerable.Range(0,k).Select(solver.RightMate).ToArray();
        long value=Enumerable.Range(0,k).Sum(i=>m.Cost[s.Matrix[i*k+match[i]]]);
        if(value!=solver.OptimalCost())throw new Exception("native assignment report");
        return match;
    }
    // Independent way to derive a dual certificate from any claimed matching:
    // solve difference constraints v[j]-v[match[i]] <= c[i,j]-c[i,match[i]].
    internal static AssignmentProof CertifyMatching(WideModel m,AssignmentShape s,int[] match,string digest)
    {
        int k=s.K;if(match.Length!=k||match.Any(j=>j<0||j>=k)||match.Distinct().Count()!=k)throw new Exception("not permutation");
        var v=new Int128[k];
        for(int pass=0;pass<k;pass++){
            bool changed=false;
            for(int i=0;i<k;i++)for(int j=0;j<k;j++){
                int a=match[i];Int128 bound=v[a]+(Int128)m.Cost[s.Matrix[i*k+j]]-m.Cost[s.Matrix[i*k+a]];
                if(bound<v[j]){v[j]=bound;changed=true;}
            }
            if(!changed)break;
            if(pass==k-1)throw new Exception("negative cycle: matching not certified optimal");
        }
        long[] u=Enumerable.Range(0,k).Select(i=>checked((long)((Int128)m.Cost[s.Matrix[i*k+match[i]]]-v[match[i]]))).ToArray();
        return new(digest,s,match,u,v.Select(x=>checked((long)x)).ToArray());
    }
    internal static WideResult Reconstruct(WideModel m,AssignmentShape s,int[] match)
    {
        int[] bits=new int[m.N];long value=0;for(int i=0;i<s.K;i++){int x=s.Matrix[i*s.K+match[i]];bits[x]=1;value+=m.Cost[x];}
        return new("Optimal",value,bits,m.N,m.Rows.Length);
    }
}
internal static class AssignmentVerifier
{
    internal static bool Verify(WideModel m,string digest,AssignmentProof proof,WideResult r)
    {
        var s=proof.Shape;int k=s.K;
        if(proof.Digest!=digest||k<3||k>316||k*k!=m.N||m.Edges.Length!=0||m.Fixes.Length!=0||m.Rows.Length!=2*k||
            s.LeftRows.Length!=k||s.RightRows.Length!=k||s.Matrix.Length!=m.N||proof.Match.Length!=k||proof.U.Length!=k||proof.V.Length!=k)return false;
        if(!s.LeftRows.Concat(s.RightRows).Order().SequenceEqual(Enumerable.Range(0,2*k))||!s.Matrix.Order().SequenceEqual(Enumerable.Range(0,m.N))||
            !proof.Match.Order().SequenceEqual(Enumerable.Range(0,k)))return false;
        if(m.Rows.Any(row=>row.L!=1||row.U!=1||row.Scope.Length!=k))return false;
        for(int i=0;i<k;i++){
            if(!m.Rows[s.LeftRows[i]].Scope.Order().SequenceEqual(s.Matrix.Skip(i*k).Take(k).Order()))return false;
            if(!m.Rows[s.RightRows[i]].Scope.Order().SequenceEqual(Enumerable.Range(0,k).Select(j=>s.Matrix[j*k+i]).Order()))return false;
        }
        if(r.Status!="Optimal"||r.Bits==null||r.Bits.Length!=m.N||r.Value==null||r.Bits.Any(b=>b is not(0 or 1)))return false;
        Int128 primal=0,dual=0;
        for(int i=0;i<k;i++){
            dual+=(Int128)proof.U[i]+proof.V[i];
            for(int j=0;j<k;j++){
                int x=s.Matrix[i*k+j];Int128 bound=(Int128)proof.U[i]+proof.V[j];
                if(bound>m.Cost[x]||r.Bits[x]!=(proof.Match[i]==j?1:0))return false;
                if(r.Bits[x]==1){primal+=m.Cost[x];if(bound!=m.Cost[x])return false;}
            }
        }
        return primal==dual&&primal==r.Value;
    }
}
