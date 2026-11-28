using ComponentStudy;
using System.Numerics;

namespace AlgorithmDiscoveryLab;

// A component owns exactly two complementary assignments. No claim that
// pair consistency satisfies omitted rows. This is distinct from certificate transport.
internal static class OrientationPartition
{
    public static List<(int[] Zero,int[] One)>? Build(int n, IEnumerable<(int A,int B)> edges)
    {
        if(n<1) throw new ArgumentOutOfRangeException(nameof(n));
        var adj=new List<int>[n]; for(int i=0;i<n;i++) adj[i]=[];
        foreach(var (a,b) in edges)
        {
            if(a<0 || a>=n || b<0 || b>=n) throw new ArgumentException("Invalid endpoint");
            adj[a].Add(b);adj[b].Add(a);
        }
        var color=new sbyte[n]; Array.Fill(color,(sbyte)-1);
        var result=new List<(int[],int[])>(); var pending=new Queue<int>();
        for(int root=0;root<n;root++)
        {
            if(color[root]>=0)continue;
            var zero=new List<int>(); var one=new List<int>();
            pending.Enqueue(root);color[root]=0;
            while(pending.TryDequeue(out int u))
            {
                (color[u]==0?zero:one).Add(u);
                foreach(int v in adj[u])
                {
                    if(color[v]==color[u])return null;
                    if(color[v]>=0)continue;
                    color[v]=(sbyte)(1-color[u]); pending.Enqueue(v);
                }
            }
            result.Add((zero.ToArray(),one.ToArray()));
        }
        return result;
    }
}
internal sealed class OrientationSearch(bool adaptive) : ICandidate
{
    private Optimum? previous;
    private readonly FullSearch warm = new(true);
    public Answer Solve(Model model)
    {
        if(model.Size is < 1 or > 12)throw new ArgumentOutOfRangeException(nameof(model));
        var prices=Corpus.Prices(model);
        int universe=(1<<model.Size)-1;
        if(model.Capacities.Any(r=>r.Scope<=0 || (r.Scope&~universe)!=0))throw new ArgumentException("Invalid scope");
        if(prices.Where(x=>x>0).Sum(x=>(long)x)>int.MaxValue ||
           prices.Where(x=>x<0).Sum(x=>(long)x)<int.MinValue)throw new ArgumentException("Fixture objective overflow");
        var edges=model.Capacities.Where(r=>r.Lower==1 && r.Upper==1 && BitOperations.PopCount((uint)r.Scope)==2)
            .Select(r=>(BitOperations.TrailingZeroCount((uint)r.Scope),BitOperations.Log2((uint)r.Scope))).ToArray();
        // Adaptive threshold is prospective and deliberately simple.
        if(adaptive && (model.Size<8 || edges.Length<2))
        {
            var result=warm.Solve(model); previous=result.Best; return result;
        }
        var partition=OrientationPartition.Build(model.Size,edges);
        if(partition==null){previous=null;return new(null,0,0,0,false,0);}
        if(adaptive && partition.Count>=model.Size-1)
        {
            var result=warm.Solve(model); previous=result.Best; return result;
        }
        var blocks=partition.Select(c=>(
            A:c.Zero.Aggregate(0,(s,i)=>s | (1<<i)),
            B:c.One.Aggregate(0,(s,i)=>s | (1<<i)),
            CA:c.Zero.Sum(i=>(long)prices[i]),
            CB:c.One.Sum(i=>(long)prices[i]))).ToArray();
        var floors=new long[blocks.Length+1];
        for(int i=blocks.Length-1;i>=0;i--)floors[i]=floors[i+1]+Math.Min(blocks[i].CA,blocks[i].CB);
        Optimum? best=previous is {} p && Oracle.Evaluate(model,p.Mask) is int value?new(p.Mask,value):null;
        int examined=0;
        void Visit(int i,int mask,long sum)
        {
            if(best is {} b && sum+floors[i]>=b.Value)return;
            if(i==blocks.Length)
            {
                examined++;
                if(model.Capacities.Any(r=>BitOperations.PopCount((uint)(mask&r.Scope)) is var k && (k<r.Lower || k>r.Upper)))return;
                best=new(mask,checked((int)sum));return;
            }
            var block=blocks[i];
            if(block.CA<=block.CB)
            {Visit(i+1,mask|block.A,sum+block.CA);Visit(i+1,mask|block.B,sum+block.CB);}
            else
            {Visit(i+1,mask|block.B,sum+block.CB);Visit(i+1,mask|block.A,sum+block.CA);}
        }
        Visit(0,0,0);
        previous=best;
        return new(best,examined,blocks.Length,0,false,0);
    }
}
