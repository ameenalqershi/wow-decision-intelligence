using ComponentStudy;

namespace AlgorithmDiscoveryLab;
internal static class ParityCorpus
{
    public static (Model Source, Model Revision) Generate(string family, int seed, int n)
    {
        var rng = new Random(seed);
        var p = Enumerable.Range(0,n).ToArray();
        rng.Shuffle(p);
        Capacity Edge(int a,int b) => new((1<<p[a]) | (1<<p[b]),1,1);
        var rows = new List<Capacity>();
        int k = n - n%2;
        switch (family)
        {
            case "star-shift":
                for(int i=1;i<n;i++) rows.Add(Edge(0,i));
                break;
            case "disjoint":
                for(int i=0;i+1<n;i+=2) rows.Add(Edge(i,i+1));
                break;
            case "isolated":
                rows.Add(Edge(0,1)); break;
            default:
                for(int i=1;i<k;i++) rows.Add(Edge(i-1,i));
                if(family=="cycle-shift") rows.Add(Edge(k-1,0));
                break;
        }
        var costs=Enumerable.Range(0,n).Select(i=>new Cost(1<<i,rng.Next(-9,10))).ToArray();
        var old=new Model(n,rows.ToArray(),costs);
        var changed=costs.ToArray();
        switch(family)
        {
            case "chain-shift": case "cycle-shift": case "star-shift":
                // A residual that is constant over the parity component: add
                // signed changes on each edge (a sum of equality multiples).
                foreach(var row in rows)
                {
                    int d=rng.Next(-5,6);
                    for(int i=0;i<n;i++) if((row.Scope & (1<<i))!=0)
                        changed[i]=changed[i] with {Weight=changed[i].Weight+d};
                }
                break;
            case "odd-cycle": rows.Add(Edge(0,2)); break;
            case "relax-chain": rows.RemoveAt(0); break;
            case "tighten": rows.Add(new Capacity(1<<p[0],0,0)); break;
            case "mixed": rows.Add(new Capacity((1<<p[0])|(1<<p[1])|(1<<p[2]),1,2));
                goto case "disjoint";
            case "isolated": case "disjoint":
                changed=costs.Select(c=>c with {Weight=c.Weight+rng.Next(-4,5)}).ToArray(); break;
            case "large-delta":
                changed=costs.Select(c=>c with {Weight=rng.Next(-1000000,1000001)}).ToArray(); break;
            default: throw new ArgumentOutOfRangeException(nameof(family));
        }
        return (old,new(n,rows.ToArray(),changed));
    }
}
