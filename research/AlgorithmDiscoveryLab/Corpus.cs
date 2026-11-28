using ComponentStudy;

namespace AlgorithmDiscoveryLab;

internal static class Corpus
{
    public static int[] Prices(Model m)
    {
        if (m.Costs.Length != m.Size || m.Costs.Where((term,i)=>term.Scope != (1<<i)).Any())
            throw new ArgumentException("ADL strategies require ordered unary costs");
        return m.Costs.Select(c=>c.Weight).ToArray();
    }

    public static (Model Source, Model Revision) Generate(string family, int seed, int pairs)
    {
        var random = new Random(seed);
        int n = 2*pairs;
        var rows = Enumerable.Range(0,pairs).Select(i=>new Capacity(3<<(2*i),1,1)).ToList();
        var costs = Enumerable.Range(0,n).Select(i=>new Cost(1<<i,random.Next(-3,11))).ToArray();
        var old = new Model(n,rows.ToArray(),costs);
        var edit = costs.ToArray();
        switch (family)
        {
            case "flat-shift": edit[0]=edit[0] with { Weight=edit[0].Weight+2 }; edit[1]=edit[1] with { Weight=edit[1].Weight+2 }; break;
            case "global-price": edit=costs.Select(c=>c with { Weight=random.Next(-8,12) }).ToArray(); break;
            case "bridge": rows.Add(new(6,0,1)); break;
            case "shortage": rows.Add(new(3,0,0)); break;
            case "dense": rows.Add(new((1<<n)-1,pairs,pairs)); edit[1]=edit[1] with {Weight=edit[1].Weight-4}; break;
            case "relax": rows.RemoveAt(0); break;
            case "outage": rows.Add(new(1,0,0)); break;
            default: throw new ArgumentOutOfRangeException(nameof(family));
        }
        return (old,new(n,rows.ToArray(),edit));
    }
}
