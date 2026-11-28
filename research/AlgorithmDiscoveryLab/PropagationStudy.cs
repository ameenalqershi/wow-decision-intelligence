using ComponentStudy;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Numerics;
namespace AlgorithmDiscoveryLab;
internal static class PropagationStudy
{
    private static IEnumerable<Model> History(int n,int seed,string family)
    {
        var rng=new Random(seed+n);var rows=new List<Capacity>();int u=(1<<n)-1;
        if(family=="chain"||family=="forced")
        {
            for(int i=0;i<n-1;i++)rows.Add(new(3<<i,1,1));
            if(family=="forced")rows.Add(new(1,1,1));
        }
        if(family=="contradiction"){rows.Add(new(3,1,1));rows.Add(new(1,1,1));rows.Add(new(2,1,1));}
        if(family=="loose"||family=="mixed")
        {
            for(int i=0;i<n;i++)
            {
                int scope=rng.Next(1,u+1),w=BitOperations.PopCount((uint)scope);
                rows.Add(new(scope,0,family=="loose"?w:Math.Max(0,w-1)));
            }
        }
        if(family=="quota")
        {
            rows.Add(new(u,n/2,n/2));
            for(int i=0;i<n-1;i+=2)rows.Add(new(3<<i,0,1));
        }
        var c=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,rng.Next(-30,31))).ToArray();
        for(int rev=0;rev<6;rev++)
        {
            if(rev>0){int j=rng.Next(n);c[j]=c[j] with {Weight=c[j].Weight+rng.Next(-15,16)};}
            if(rev==2)rows.Add(new(1,0,0));
            if(rev==3)rows.RemoveAt(rows.Count-1);
            if(rev==4)rows.Reverse();
            yield return new(n,rows.ToArray(),c.ToArray());
        }
    }
    internal static void Run(string split)
    {
        bool dev=split=="dev";if(!dev&&split!="holdout")throw new ArgumentException("bad split");
        string[] names=["full","warm","orientation","adaptive","propagation"];
        ICandidate Create(string s)=>s switch{"full"=>new FullSearch(false),"warm"=>new FullSearch(true),"orientation"=>new OrientationSearch(false),"adaptive"=>new AdaptiveDecision(true),"propagation"=>new PropagationSearch(),_=>throw new ArgumentException()};
        foreach(string name in names){var a=Create(name);foreach(var m in History(8,375101,"mixed"))_=a.Solve(AdaptiveDecisionStudy.Admit(m));}
        foreach(int n in new[]{6,10,12})
        foreach(string family in new[]{"chain","forced","contradiction","loose","mixed","quota"})
        for(int i=0;i<(dev?4:16);i++)
        {
            int seed=(dev?375101:694301)+i,rev=0;
            var arms=names.ToDictionary(x=>x,Create);
            foreach(var input in History(n,seed,family))
            {
                int? expected=Oracle.Minimum(input);
                foreach(string name in names.Skip((seed+rev)%names.Length).Concat(names.Take((seed+rev)%names.Length)))
                {
                    long alloc=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                    var m=AdaptiveDecisionStudy.Admit(input);
                    string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m)));
                    var result=arms[name].Solve(m);
                    int? checkedCost=result.Best is {} b?Oracle.Evaluate(m,b.Mask):null;
                    bool witness=result.Best==null||checkedCost==result.Best.Value;
                    byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{digest,revision=rev,result.Best,result.Transported,route=name,certificate=(string?)null,bound=(long?)null});
                    long end=Stopwatch.GetTimestamp();
                    bool valid=witness&&result.Best?.Value==expected&&checkedCost==expected;
                    var p=arms[name] as PropagationSearch;
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision=rev,name,value=result.Best?.Value,expected,valid,digest,result.Transported,result.Examined,nodes=p?.Nodes,forced=p?.Forced,contradictions=p?.Contradictions,ticks=end-start,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=evidence.Length}));
                    if(!valid)throw new Exception($"Mismatch {family} {seed} {rev} {name}");
                }
                rev++;
            }
        }
    }
}
