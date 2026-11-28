using ComponentStudy;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Numerics;
namespace AlgorithmDiscoveryLab;
internal static class StructuralAdmissionStudy
{
    internal static IEnumerable<Model> History(int n,int seed,string family)
    {
        var rng=new Random(seed+n);int u=(1<<n)-1;
        var rows=new List<Capacity>();
        int[] perm=Enumerable.Range(0,n).OrderBy(_=>rng.Next()).ToArray();
        int Bit(int i)=>1<<perm[i];
        void Pair(int a,int b)=>rows.Add(new(Bit(a)|Bit(b),1,1));
        if(family=="chain")for(int i=1;i<n;i++)Pair(i-1,i);
        if(family=="star")for(int i=1;i<n;i++)Pair(0,i);
        if(family=="matching"||family=="workforce"||family=="hybrid")for(int i=1;i<n;i+=2)Pair(i-1,i);
        if(family=="odd") {Pair(0,1);Pair(1,2);Pair(2,0);}
        if(family=="fixed") {rows.Add(new(u&~Bit(0),0,0));rows.Add(new(Bit(0),1,1));}
        if(family=="workforce"||family=="hybrid")rows.Add(new(u,n/2,n/2));
        if(family=="packing"||family=="hybrid")
            for(int j=0;j<n/2;j++){int scope=rng.Next(1,u+1),w=BitOperations.PopCount((uint)scope);rows.Add(new(scope,0,w/2));}
        if(family=="workforce")rows.Add(new(Bit(0),0,0));
        if(family=="free")rows.Add(new(u,0,n));
        if(family=="loose")for(int j=0;j<n;j++){int scope=rng.Next(1,u+1);rows.Add(new(scope,0,BitOperations.PopCount((uint)scope)));}
        var c=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,rng.Next(-40,41))).ToArray();
        for(int rev=0;rev<8;rev++)
        {
            if(rev>0){int j=rng.Next(n);c[j]=c[j] with {Weight=c[j].Weight+rng.Next(-20,21)};}
            if(rev==2)rows.Add(new(Bit(0),0,0));
            if(rev==3)rows.Add(new(Bit(0),1,1));
            if(rev==4)rows.RemoveAt(rows.Count-1);
            if(rev==5)rows.RemoveAt(rows.Count-1);
            if(rev==6){rows.Reverse();rows.Add(new(Bit(0)|Bit(1)|Bit(2),1,2));}
            if(rev==7)rows.RemoveAt(rows.Count-1);
            yield return new(n,rows.ToArray(),c.ToArray());
        }
    }
    internal static void Run(string split)
    {
        bool dev=split=="dev";if(!dev&&split!="holdout")throw new ArgumentException("split");
        string[] families=dev?["chain","fixed","loose","packing"]:["star","matching","odd","workforce","hybrid","free"];
        string[] names=["full","warm","orientation","propagation","gate-only","structural"];
        ICandidate Create(string s)=>s switch{"full"=>new FullSearch(false),"warm"=>new FullSearch(true),"orientation"=>new OrientationSearch(false),"propagation"=>new PropagationSearch(),"gate-only"=>new StructuralAdmission(false),"structural"=>new StructuralAdmission(true),_=>throw new ArgumentException()};
        foreach(string name in names){var a=Create(name);foreach(var m in History(8,891001,"packing"))_=a.Solve(AdaptiveDecisionStudy.Admit(m));}
        foreach(int n in new[]{6,10,12})
        foreach(string family in families)
        for(int i=0;i<(dev?4:16);i++)
        {
            int seed=(dev?891001:923001)+i,rev=0;
            var arms=names.ToDictionary(x=>x,Create);
            foreach(var input in History(n,seed,family))
            {
                int? expected=Oracle.Minimum(input);
                foreach(string name in names.Skip((seed+rev)%names.Length).Concat(names.Take((seed+rev)%names.Length)))
                {
                    long alloc=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();
                    var m=AdaptiveDecisionStudy.Admit(input);
                    string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m)));
                    long admitted=Stopwatch.GetTimestamp();
                    var result=arms[name].Solve(m);
                    long solved=Stopwatch.GetTimestamp();
                    int? cost=result.Best is {} b?Oracle.Evaluate(m,b.Mask):null;
                    bool valid=result.Best?.Value==expected&&cost==expected;
                    byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{digest,revision=rev,result.Best,result.Transported});
                    long end=Stopwatch.GetTimestamp();
                    var p=arms[name] as StructuralAdmission;
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,revision=rev,name,value=result.Best?.Value,expected,valid,digest,result.Transported,result.Examined,route=p?.Route??name,selectionTicks=p?.SelectionTicks,pairCoverage=p?.PairCoverage,fixedCoverage=p?.FixedCoverage,admissionTicks=admitted-start,solveTicks=solved-admitted,verifyEvidenceTicks=end-solved,ticks=end-start,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=evidence.Length}));
                    if(!valid)throw new Exception($"Mismatch {family} {seed} {rev} {name}");
                }
                rev++;
            }
        }
    }
}
