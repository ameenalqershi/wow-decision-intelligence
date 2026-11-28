using System.Diagnostics;
using System.Text.Json;
namespace AlgorithmDiscoveryLab;

internal static class NormalizedQuotaStudy
{
    private sealed record Case(long[] D,(int A,int B)[] E,CardinalityResidual.Row Q);
    private static Case Generate(int n,int seed,string family)
    {
        var r=new Random(seed);
        var d=Enumerable.Range(0,n).Select(_=>(long)r.Next(-100,101)).ToArray();
        var edges=new List<(int,int)>();
        var q=new bool[n];
        int k;
        switch(family)
        {
            case "unit-free":
                for(int i=0;i<n;i++)q[i]=true;
                k=r.Next(n+1);break;
            case "unit-pairs":
                for(int i=0;i+1<n;i+=2){edges.Add((i,i+1));q[i]=true;}
                k=r.Next(n/2+1);break;
            case "equal-pairs":
                for(int i=0;i+1<n;i+=2){edges.Add((i,i+1));q[i]=q[i+1]=true;}
                k=n/2+(seed%2);break;
            case "uniform-stars":
                for(int i=0;i+3<n;i+=4){for(int j=1;j<4;j++){edges.Add((i,i+j));q[i+j]=true;}}
                k=3*r.Next(n/4+1)+(seed%2);break;
            case "mixed-blocks":
                for(int i=0;i<n;){int size=Math.Min(n-i,r.Next(1,8));for(int j=1;j<size;j++)edges.Add((i,i+j));i+=size;}
                for(int i=0;i<n;i++)q[i]=r.Next(2)==0;
                k=r.Next(n+1);break;
            default:
                for(int i=0;i<n;i++)q[i]=r.Next(2)==0;
                for(int i=0;i<n*2;i++)if(r.Next(3)==0)edges.Add((r.Next(n),r.Next(n)));
                k=r.Next(n+1);break;
        }
        return new(d,edges.ToArray(),new(q,k));
    }
    private static long? Oracle(Case c)
    {
        long? best=null;
        for(int m=0;m<(1<<c.D.Length);m++)
        {
            if(c.E.Any(e=>((m>>e.A)&1)==((m>>e.B)&1)))continue;
            long sum=0;int k=0;
            for(int i=0;i<c.D.Length;i++)if(((m>>i)&1)!=0){sum+=c.D[i];if(c.Q.Member[i])k++;}
            if(k==c.Q.Count && (best==null || sum<best))best=sum;
        }
        return best;
    }
    private static long? WideOracle(Case c,string family)
    {
        int n=c.D.Length,k=c.Q.Count;
        if(family=="unit-free")return c.D.Order().Take(k).Sum();
        if(family=="unit-pairs")return Enumerable.Range(0,n/2).Sum(i=>c.D[2*i+1])+Enumerable.Range(0,n/2).Select(i=>c.D[2*i]-c.D[2*i+1]).Order().Take(k).Sum();
        if(family=="equal-pairs")return k==n/2?Enumerable.Range(0,n/2).Sum(i=>Math.Min(c.D[2*i],c.D[2*i+1])):null;
        if(family=="uniform-stars")return k%3!=0 || k/3>n/4?null:Enumerable.Range(0,n/4).Sum(i=>c.D[4*i])+Enumerable.Range(0,n/4).Select(i=>c.D[4*i+1]+c.D[4*i+2]+c.D[4*i+3]-c.D[4*i]).Order().Take(k/3).Sum();
        throw new ArgumentException("No independent wide oracle");
    }
    internal static void Run(string split)
    {
        bool dev=split=="dev";
        if(!dev && split!="holdout")throw new ArgumentException("Invalid split");
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var families=new[]{"unit-free","unit-pairs","equal-pairs","uniform-stars","mixed-blocks","adversarial"};
        int seedBase=dev?615001:817203;
        // Warm both implementations using development data, not measured.
        var warm=Generate(12,615001,"mixed-blocks");
        for(int i=0;i<24;i++){_=CardinalityResidual.Minimum(warm.D,warm.E,warm.Q);_=NormalizedQuota.Minimum(warm.D,warm.E,warm.Q);}
        foreach(int n in new[]{4,8,12,64,512,4096})
        foreach(string family in families)
        {
            if(n>12 && family=="adversarial")continue;
            for(int index=0;index<(n<=12?(dev?16:128):(dev?3:12));index++)
            {
                int seed=seedBase+index;
                var c=Generate(n,seed,family);
                bool independent=n<=12||family!="mixed-blocks";
                long? expected=n<=12?Oracle(c):independent?WideOracle(c,family):CardinalityResidual.Minimum(c.D,c.E,c.Q,cancel.Token);
                for(int rep=0;rep<(dev?1:3);rep++)
                foreach(bool fast in (seed+rep)%2==0?new[]{false,true}:new[]{true,false})
                {
                    long before=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                    long? value=fast?NormalizedQuota.Minimum(c.D,c.E,c.Q,cancel.Token):CardinalityResidual.Minimum(c.D,c.E,c.Q,cancel.Token);
                    long elapsed=Stopwatch.GetTimestamp()-start,bytes=GC.GetAllocatedBytesForCurrentThread()-before;
                    bool valid=value==expected;
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,rep,fast,k=c.Q.Count,edges=c.E.Length,value,expected,independent,valid,ticks=elapsed,frequency=Stopwatch.Frequency,bytes}));
                    if(!valid)throw new InvalidOperationException($"Mismatch {n} {family} {seed}");
                }
            }
        }
    }
}
