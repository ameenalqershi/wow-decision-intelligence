namespace AlgorithmDiscoveryLab;

// Single-owner cache. Caller inputs may change between calls, not during a call.
// Exact structure comparison (no hash-only admission); no objective result reused.
internal sealed class CachedQuotaResidual
{
    private sealed record Block(int[] A,int[] B,int Weight);
    private sealed record Plan(int N,(int A,int B)[] Edges,bool[] Members,int K,
        Block[]? Blocks,int Target,int Gcd,bool Complement,bool Unit);
    private Plan? plan;
    internal int Builds { get; private set; }
    internal int Hits { get; private set; }
    internal long? Minimum(long[] delta,(int A,int B)[] edges,CardinalityResidual.Row quota,CancellationToken cancel=default)
    {
        int n=delta.Length;
        if(n<1)throw new ArgumentOutOfRangeException(nameof(delta));
        if(quota.Member.Length!=n||quota.Count<0||quota.Count>n)throw new ArgumentException("Invalid quota");
        Int128 magnitude=0;
        foreach(long d in delta)magnitude+=Int128.Abs(d);
        if(magnitude>=long.MaxValue)throw new OverflowException("Residual exceeds envelope");
        cancel.ThrowIfCancellationRequested();
        if(plan is not {} p || p.N!=n || p.K!=quota.Count ||
           !p.Edges.AsSpan().SequenceEqual(edges) || !p.Members.AsSpan().SequenceEqual(quota.Member))
        {
            p=Build(n,edges,quota,cancel);
            plan=p;Builds++;
        }
        else Hits++;
        if(p.Blocks==null)return null;
        long baseCost=0;
        var differences=new List<(int Weight,long Cost)>();
        foreach(var block in p.Blocks)
        {
            cancel.ThrowIfCancellationRequested();
            long a=0,b=0;
            foreach(int i in block.A)a+=delta[i];
            foreach(int i in block.B)b+=delta[i];
            if(block.Weight==0){baseCost+=Math.Min(a,b);continue;}
            if(p.Complement){baseCost+=b;differences.Add((block.Weight,-(b-a)));}
            else{baseCost+=a;differences.Add((block.Weight,b-a));}
        }
        if(p.Target==0)return baseCost;
        if(p.Unit)
        {
            var costs=differences.Select(x=>x.Cost).ToArray();Array.Sort(costs);
            for(int i=0;i<p.Target;i++)baseCost+=costs[i];
            return baseCost;
        }
        var dp=new long[p.Target+1];Array.Fill(dp,long.MaxValue);dp[0]=0;
        int reached=0;
        foreach(var (weight,cost) in differences)
        {
            cancel.ThrowIfCancellationRequested();
            int w=weight/p.Gcd;if(w>p.Target)continue;
            int hi=Math.Min(p.Target,reached+w);
            for(int j=hi;j>=w;j--)
                if(dp[j-w]!=long.MaxValue)dp[j]=Math.Min(dp[j],dp[j-w]+cost);
            reached=hi;
        }
        return dp[p.Target]==long.MaxValue?null:baseCost+dp[p.Target];
    }
    private static Plan Build(int n,(int A,int B)[] edges,CardinalityResidual.Row quota,CancellationToken cancel)
    {
        var snapshot=edges.ToArray();var members=quota.Member.ToArray();
        var parts=OrientationPartition.Build(n,snapshot);
        Plan Impossible()=>new(n,snapshot,members,quota.Count,null,0,1,false,false);
        if(parts==null)return Impossible();
        var blocks=new List<Block>();int target=quota.Count,total=0,gcd=0;
        foreach(var (zero,one) in parts)
        {
            cancel.ThrowIfCancellationRequested();
            int a=zero.Count(i=>members[i]),b=one.Count(i=>members[i]);
            if(a>b){blocks.Add(new(one,zero,a-b));target-=b;total+=a-b;gcd=Gcd(gcd,a-b);}
            else{blocks.Add(new(zero,one,b-a));target-=a;total+=b-a;gcd=Gcd(gcd,b-a);}
        }
        if(target<0||target>total)return Impossible();
        if(gcd==0)return new(n,snapshot,members,quota.Count,blocks.ToArray(),0,1,false,true);
        if(target%gcd!=0)return Impossible();
        bool complement=target>total-target;
        if(complement)target=total-target;
        return new(n,snapshot,members,quota.Count,blocks.ToArray(),target/gcd,gcd,complement,
            blocks.All(b=>b.Weight==0||b.Weight==gcd));
    }
    private static int Gcd(int a,int b){while(b!=0){(a,b)=(b,a%b);}return a;}
}
