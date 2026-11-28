using ComponentStudy;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Numerics;
namespace AlgorithmDiscoveryLab;
internal static class CompressedPropagationStudy
{
    internal static IEnumerable<Model> History(int n,int seed,string family)
    {
        var r=new Random(seed+n);int u=(1<<n)-1;
        var p=Enumerable.Range(0,n).OrderBy(_=>r.Next()).ToArray();
        int B(int i)=>1<<p[i];
        var rows=new List<Capacity>();
        void Pair(int a,int b)=>rows.Add(new(B(a)|B(b),1,1));
        if(family is "forest" or "linked-quota" or "coupled")for(int i=1;i<n;i++)if(i%3!=0)Pair(i-1,i);
        if(family=="odd-mixed"){Pair(0,1);Pair(1,2);Pair(2,0);rows.Add(new(u,1,n-1));}
        if(family is "matching-quota" or "shift-proxy")for(int i=1;i<n;i+=2)Pair(i-1,i);
        if(family is "linked-quota" or "matching-quota" or "shift-proxy")
        {
            int scope=0;for(int i=0;i<n;i+=2)scope|=B(i);
            rows.Add(new(scope,n/4,n/4));
        }
        if(family is "coupled" or "packing" or "shift-proxy" or "dense")
        {
            for(int j=0;j<n/2;j++){int scope=r.Next(1,u+1),w=BitOperations.PopCount((uint)scope);rows.Add(new(scope,w/3,(2*w+2)/3));}
        }
        if(family=="dense")for(int i=1;i<n;i++)Pair(i-1,i);
        if(family=="free")rows.Add(new(u,0,n));
        if(family=="fixed")rows.Add(new(u&~B(0),0,0));
        var c=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,r.Next(-50,51))).ToArray();
        for(int rev=0;rev<8;rev++)
        {
            if(rev>0){int j=r.Next(n);c[j]=c[j] with {Weight=c[j].Weight+r.Next(-30,31)};}
            if(rev==1)rows.Add(new(B(0),0,0));
            if(rev==2)rows.Add(new(B(0),1,1));
            if(rev==3)rows.RemoveAt(rows.Count-1);
            if(rev==4)rows.RemoveAt(rows.Count-1);
            if(rev==5)rows.Add(new(B(0)|B(1)|B(2),1,2));
            if(rev==6)rows.Reverse();
            if(rev==7)rows.Add(rows[0]);
            yield return new(n,rows.ToArray(),c.ToArray());
        }
    }
    internal static void Run(string split)
    {
        bool dev=split=="dev";if(!dev&&split!="holdout")throw new ArgumentException("split");
        string[] families=dev?["forest","linked-quota","packing","fixed"]:["odd-mixed","matching-quota","shift-proxy","coupled","dense","free"];
        string[] names=["full","warm","orientation","propagation","structural","quotient-only","compressed"];
        ICandidate Create(string s)=>s switch{"full"=>new FullSearch(false),"warm"=>new FullSearch(true),"orientation"=>new OrientationSearch(false),"propagation"=>new PropagationSearch(),"structural"=>new StructuralAdmission(true),"quotient-only"=>new CompressedPropagation(false),"compressed"=>new CompressedPropagation(true),_=>throw new ArgumentException()};
        foreach(string name in names){var a=Create(name);foreach(var m in History(8,1140101,"packing"))_=a.Solve(AdaptiveDecisionStudy.Admit(m));}
        foreach(int n in new[]{6,10,12})
        foreach(string family in families)
        for(int i=0;i<(dev?4:16);i++)
        {
            int seed=(dev?1140101:1170101)+i,rev=0;
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
                    int? cost=result.Best is {} b?Oracle.Evaluate(m,b.Mask):null;
                    bool valid=result.Best?.Value==expected&&cost==expected;
                    byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{digest,revision=rev,result.Best,result.Transported});
                    long end=Stopwatch.GetTimestamp();
                    var p=arms[name] as CompressedPropagation;
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision=rev,name,value=result.Best?.Value,expected,valid,digest,result.Transported,result.Examined,route=p?.Route??name,nodes=p?.Nodes,blocks=p?.Blocks,forced=p?.Forced,hintAccepted=p?.HintAccepted,ticks=end-start,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=evidence.Length}));
                    if(!valid)throw new Exception($"Mismatch {family} {seed} {rev} {name}");
                }
                rev++;
            }
        }
    }
}
