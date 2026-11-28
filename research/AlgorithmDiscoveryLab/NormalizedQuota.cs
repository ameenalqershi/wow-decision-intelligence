namespace AlgorithmDiscoveryLab;

// Exact residual over opposite-bit equations and one exact quota.
// Same checked input envelope as the companion baseline.
internal static class NormalizedQuota
{
    internal static long? Minimum(long[] delta, IReadOnlyList<(int A,int B)> edges,
        CardinalityResidual.Row quota, CancellationToken cancel = default)
    {
        int n=delta.Length;
        if(quota.Member.Length!=n || quota.Count<0 || quota.Count>n) throw new ArgumentException("Invalid quota");
        Int128 magnitude=0;
        foreach(long d in delta) magnitude+=Int128.Abs(d);
        if(magnitude>=long.MaxValue) throw new OverflowException("Residual exceeds envelope");
        cancel.ThrowIfCancellationRequested();
        var blocks=OrientationPartition.Build(n,edges);
        if(blocks==null)return null;
        var choices=new List<(int W,long D)>();
        long baseCost=0; int target=quota.Count, total=0, gcd=0;
        foreach(var (zero,one) in blocks)
        {
            cancel.ThrowIfCancellationRequested();
            long a=0,b=0; int ka=0,kb=0;
            foreach(int i in zero){a+=delta[i];if(quota.Member[i])ka++;}
            foreach(int i in one){b+=delta[i];if(quota.Member[i])kb++;}
            if(ka==kb){target-=ka;baseCost+=Math.Min(a,b);continue;}
            if(ka>kb){(a,b)=(b,a);(ka,kb)=(kb,ka);}
            target-=ka;baseCost+=a;
            int w=kb-ka; long d=b-a;
            choices.Add((w,d));total+=w;gcd=Gcd(gcd,w);
        }
        if(target<0 || target>total)return null;
        if(choices.Count==0)return baseCost;
        if(target%gcd!=0)return null;
        // Complement the selections to minimize the DP target.
        if(target>total-target)
        {
            target=total-target;
            for(int i=0;i<choices.Count;i++){var (w,d)=choices[i];baseCost+=d;choices[i]=(w,-d);}
        }
        target/=gcd;
        if(target==0)return baseCost;
        if(choices.All(c=>c.W==gcd))
        {
            var costs=choices.Select(c=>c.D).ToArray();Array.Sort(costs);
            for(int i=0;i<target;i++)baseCost+=costs[i];
            return baseCost;
        }
        var dp=new long[target+1];Array.Fill(dp,long.MaxValue);dp[0]=0;
        int reached=0;
        foreach(var (weight,d) in choices)
        {
            cancel.ThrowIfCancellationRequested();
            int w=weight/gcd; if(w>target)continue;
            int hi=Math.Min(target,reached+w);
            for(int j=hi;j>=w;j--)
                if(dp[j-w]!=long.MaxValue)dp[j]=Math.Min(dp[j],dp[j-w]+d);
            reached=hi;
        }
        return dp[target]==long.MaxValue?null:baseCost+dp[target];
    }
    private static int Gcd(int a,int b){while(b!=0){(a,b)=(b,a%b);}return a;}
}
