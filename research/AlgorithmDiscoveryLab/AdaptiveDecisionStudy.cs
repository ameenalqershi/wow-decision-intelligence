using ComponentStudy;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Numerics;
namespace AlgorithmDiscoveryLab;

internal static class AdaptiveDecisionStudy
{
    internal static Model Admit(Model m)
    {
        if(m.Size is <1 or >12)throw new ArgumentOutOfRangeException(nameof(m));
        int u=(1<<m.Size)-1;
        foreach(var r in m.Capacities)
        {
            int w=BitOperations.PopCount((uint)r.Scope);
            if(r.Scope<=0||(r.Scope&~u)!=0||r.Lower<0||r.Upper<r.Lower||r.Upper>w)throw new ArgumentException("invalid row");
        }
        var prices=Corpus.Prices(m);
        if(prices.Where(x=>x>0).Sum(x=>(long)x)>int.MaxValue||prices.Where(x=>x<0).Sum(x=>(long)x)<int.MinValue)throw new OverflowException();
        return new(m.Size,m.Capacities.ToArray(),m.Costs.ToArray());
    }
    internal static IEnumerable<Model> History(int n,int seed,string family)
    {
        var r=new Random(seed+n);var rows=new List<Capacity>();
        for(int i=0;i<n-1;i+=2)if(family!="sparse"||r.Next(3)==0)rows.Add(new(3<<i,1,1));
        if(family=="quota"||family=="edits")
        {
            int mask=0;for(int i=0;i<n;i+=2)mask|=1<<i;
            rows.Add(new(mask,n/4,n/4));
        }
        if(family=="mixed")
        {
            for(int i=0;i<3;i++)
            {
                int mask=r.Next(1,1<<n),width=BitOperations.PopCount((uint)mask);
                rows.Add(new(mask,0,Math.Max(1,width-1)));
            }
        }
        var costs=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,r.Next(-20,21))).ToArray();
        for(int rev=0;rev<8;rev++)
        {
            if(rev>0)
            {
                if(family=="flat"||rev%4==1)
                {
                    int pair=r.Next(n/2)*2;int shift=r.Next(-5,6);
                    costs[pair]=costs[pair] with {Weight=costs[pair].Weight+shift};
                    costs[pair+1]=costs[pair+1] with {Weight=costs[pair+1].Weight+shift};
                }
                else if(rev%4==2)
                {
                    for(int i=0;i<n;i++)costs[i]=costs[i] with {Weight=costs[i].Weight+r.Next(-9,10)};
                }
                else if(rev%4==3)
                {
                    int j=r.Next(n);costs[j]=costs[j] with {Weight=costs[j].Weight+r.Next(-8,9)};
                }
                if(family=="edits")
                {
                    if(rev==2)rows.Add(new(1,0,0));
                    if(rev==3)rows.RemoveAt(rows.Count-1);
                    if(rev==4)rows.Reverse();
                    if(rev==5)rows.Add(rows[0]);
                    if(rev==6)rows.RemoveAt(0);
                    if(rev==7)rows.Add(new(1,1,1));
                }
                if(family=="mixed"&&rev==5)rows.Add(new(1,0,0));
            }
            yield return new(n,rows.ToArray(),costs.ToArray());
        }
    }
    internal static void Run(string split)
    {
        bool dev=split=="dev";if(!dev&&split!="holdout")throw new ArgumentException("invalid split");
        string[] names=["full","warm","orientation","parity-warm","component-reuse","hierarchy","adaptive"];
        ICandidate Create(string s)=>s switch{
            "full"=>new FullSearch(false),"warm"=>new FullSearch(true),"orientation"=>new OrientationSearch(false),
            "parity-warm"=>new ParityTransfer(true),"component-reuse"=>new Components(true),
            "hierarchy"=>new AdaptiveDecision(false),"adaptive"=>new AdaptiveDecision(true),_=>throw new ArgumentException()};
        foreach(var name in names){var a=Create(name);foreach(var m in History(8,246701,"mixed"))_=a.Solve(Admit(m));}
        using var process=Process.GetCurrentProcess();
        foreach(int n in new[]{6,10,12})
        foreach(string family in new[]{"flat","sparse","quota","edits","mixed"})
        for(int s=0;s<(dev?4:16);s++)
        {
            int seed=(dev?246701:572011)+s;
            var arms=names.ToDictionary(x=>x,Create);int rev=0;
            foreach(var input in History(n,seed,family))
            {
                int? oracle=Oracle.Minimum(input);
                foreach(string name in names.Skip((seed+rev)%names.Length).Concat(names.Take((seed+rev)%names.Length)))
                {
                    long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                    var model=Admit(input);
                    // Bind identical evidence fields to the exact input snapshot for every arm.
                    string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(model)));
                    long admitted=Stopwatch.GetTimestamp();
                    var arm=arms[name];var answer=arm.Solve(model);
                    long solved=Stopwatch.GetTimestamp();
                    int? checkedCost=answer.Best is {} best?Oracle.Evaluate(model,best.Mask):null;
                    bool witness=answer.Best==null||checkedCost==answer.Best.Value;
                    if(!witness)throw new Exception("invalid witness");
                    // Serialization to in-memory bytes is included for every arm;
                    // durable disk persistence and independent optimum checking are not.
                    var a=arm as AdaptiveDecision;
                    byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{digest,revision=rev,answer.Best,answer.Transported,route=a?.Route??name,certificate=a?.Certificate,bound=a?.Bound});
                    long end=Stopwatch.GetTimestamp();
                    bool valid=answer.Best?.Value==oracle&&checkedCost==oracle;
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision=rev,name,value=answer.Best?.Value,oracle,valid,digest,answer.Transported,answer.Examined,answer.Hits,answer.Components,route=a?.Route??name,reason=a?.Reason,certificate=a?.Certificate,attempts=a?.Attempts,workEstimate=a?.WorkEstimate,bound=a?.Bound,cacheHit=a?.CacheHit,solved=a?.Solved,admissionTicks=admitted-start,solveTicks=solved-admitted,verifyEvidenceTicks=end-solved,ticks=end-start,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-allocated,evidenceBytes=evidence.Length,peakProcessBytes=process.PeakWorkingSet64}));
                    if(!valid)throw new Exception($"Mismatch {family} {seed} {rev} {name}");
                }
                rev++;
            }
        }
    }
}
