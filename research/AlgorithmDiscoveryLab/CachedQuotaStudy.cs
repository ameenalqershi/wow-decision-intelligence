using System.Diagnostics;
using System.Text.Json;
namespace AlgorithmDiscoveryLab;
internal static class CachedQuotaStudy
{
    private static long? Oracle(long[] d,(int A,int B)[] edges,CardinalityResidual.Row q)
    {
        long? best=null;
        for(int m=0;m<(1<<d.Length);m++){
            if(edges.Any(e=>((m>>e.A)&1)==((m>>e.B)&1)))continue;
            int count=0;long cost=0;
            for(int i=0;i<d.Length;i++)if(((m>>i)&1)!=0){cost+=d[i];if(q.Member[i])count++;}
            if(count==q.Count&&(best==null||cost<best))best=cost;
        }
        return best;
    }
    internal static void Run(string split)
    {
        bool dev=split=="dev";if(!dev&&split!="holdout")throw new ArgumentException("Invalid split");
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        // Pre-measurement deterministic warm-up and mutation check.
        var test=new CachedQuotaResidual();var e0=new (int A,int B)[]{(0,1)};var m0=new[]{true,false};
        if(test.Minimum([4,-2],e0,new(m0,1))!=4)throw new Exception("initial");
        m0[0]=false;m0[1]=true;
        if(test.Minimum([4,-2],e0,new(m0,1))!=-2||test.Builds!=2)throw new Exception("membership mutation");
        e0[0]=(0,0);
        if(test.Minimum([4,-2],e0,new(m0,1))!=null||test.Builds!=3)throw new Exception("edge mutation");
        for(int i=0;i<16;i++)_=test.Minimum([4,-2],e0,new(m0,1));
        foreach(int n in new[]{8,64,512,4096})
        foreach(string family in new[]{"stable-unit","stable-mixed","revision-edits"})
        for(int s=0;s<(dev?4:16);s++)
        {
            int seed=(dev?481901:739301)+s;
            var rng=new Random(seed+n);var e=new List<(int A,int B)>();
            var members=new bool[n];int quota=0;
            for(int i=0;i<n;)
            {
                int size=Math.Min(n-i,family=="stable-unit"?2:rng.Next(1,9));
                for(int j=1;j<size;j++)e.Add((i,i+j));
                for(int j=0;j<size;j++)members[i+j]=family=="stable-unit"?j==0:rng.Next(2)==0;
                if(members[i])quota++;
                i+=size;
            }
            var initial=e.ToArray();
            var edgeCopy=initial.ToArray();
            var cached=new CachedQuotaResidual();
            int priorBuilds=0;
            for(int revision=0;revision<10;revision++)
            {
                if(family=="revision-edits")
                {
                    if(revision==2 && edgeCopy.Length>0)edgeCopy[0]=(0,0);
                    if(revision==3)edgeCopy=initial.ToArray();
                    if(revision==4)Array.Reverse(edgeCopy);
                    if(revision==5)members[0]=!members[0];
                    if(revision==7)quota=Math.Min(n,quota+1);
                    if(revision==8)edgeCopy=edgeCopy.Skip(1).ToArray();
                }
                var q=new CardinalityResidual.Row(members,quota);
                long[] d=Enumerable.Range(0,n).Select(_=>(long)rng.Next(-100,101)).ToArray();
                long? reference=n<=8?Oracle(d,edgeCopy,q):CardinalityResidual.Minimum(d,edgeCopy,q,limit.Token);
                foreach(bool cache in (seed+revision)%2==0?new[]{false,true}:new[]{true,false})
                {
                    long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();
                    long? value=cache?cached.Minimum(d,edgeCopy,q,limit.Token):NormalizedQuota.Minimum(d,edgeCopy,q,limit.Token);
                    long elapsed=Stopwatch.GetTimestamp()-start,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc;
                    bool valid=value==reference;
                    bool? hit=cache?cached.Builds==priorBuilds:null;
                    if(cache)priorBuilds=cached.Builds;
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision,cache,hit,value,reference,valid,independent=n<=8,ticks=elapsed,frequency=Stopwatch.Frequency,bytes}));
                    if(!valid)throw new Exception($"Mismatch {n} {family} {seed} {revision}");
                }
            }
        }
    }
}
