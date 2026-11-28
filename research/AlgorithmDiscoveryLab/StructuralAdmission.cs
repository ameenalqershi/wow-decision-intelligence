using ComponentStudy;
using System.Numerics;
using System.Diagnostics;
namespace AlgorithmDiscoveryLab;

// Exact on admitted Boolean unary-cost grammar. No learned accuracy claim.
internal sealed class StructuralAdmission(bool direct) : ICandidate
{
    private readonly FullSearch warm=new(true);
    private readonly PropagationSearch propagation=new();
    internal string Route {get;private set;}="";
    internal long SelectionTicks {get;private set;}
    internal int PairCoverage {get;private set;}
    internal int FixedCoverage {get;private set;}
    public Answer Solve(Model m)
    {
        long start=Stopwatch.GetTimestamp();
        int n=m.Size, pairMask=0, ones=0, zeros=0, general=0;
        Span<int> adj=stackalloc int[n];
        adj.Clear();
        foreach(var r in m.Capacities)
        {
            int w=BitOperations.PopCount((uint)r.Scope);
            if(r.Lower==0&&r.Upper==w)continue;
            if(r.Upper==0){zeros|=r.Scope;continue;}
            if(r.Lower==w){ones|=r.Scope;continue;}
            if(w==2&&r.Lower==1&&r.Upper==1)
            {
                pairMask|=r.Scope;
                int a=BitOperations.TrailingZeroCount((uint)r.Scope),b=BitOperations.Log2((uint)r.Scope);
                adj[a]|=1<<b;adj[b]|=1<<a;
            }
            else general++;
        }
        PairCoverage=BitOperations.PopCount((uint)pairMask);
        FixedCoverage=BitOperations.PopCount((uint)(ones|zeros));
        Route=direct&&general==0?"direct":((ones&zeros)!=0||PairCoverage*2>=n||FixedCoverage*3>=n)?"propagation":"warm";
        SelectionTicks=Stopwatch.GetTimestamp()-start;
        if(Route=="warm")return warm.Solve(m);
        if(Route=="propagation")return propagation.Solve(m);
        if((ones&zeros)!=0)return new(null,0,0,0,false,SelectionTicks);
        int remaining=(1<<n)-1,mask=0,components=0;
        long cost=0;var prices=Corpus.Prices(m);
        while(remaining!=0)
        {
            int root=remaining&-remaining,a=root,b=0,front=root;
            // Flood by alternating colors. Every visited edge must join opposite colors.
            while(front!=0)
            {
                int next=0,scan=front;
                while(scan!=0){int bit=scan&-scan;scan^=bit;next|=adj[BitOperations.TrailingZeroCount((uint)bit)];}
                if((front&a)!=0)
                {
                    if((next&a)!=0)return new(null,0,components,0,false,SelectionTicks);
                    next&=~b;b|=next;
                }
                else
                {
                    if((next&b)!=0)return new(null,0,components,0,false,SelectionTicks);
                    next&=~a;a|=next;
                }
                front=next;
            }
            int component=a|b;
            bool okA=(a&zeros)==0&&(b&ones)==0,okB=(b&zeros)==0&&(a&ones)==0;
            if(!okA&&!okB)return new(null,0,components,0,false,SelectionTicks);
            long ca=0,cb=0;
            for(int i=0;i<n;i++){if((a&(1<<i))!=0)ca+=prices[i];if((b&(1<<i))!=0)cb+=prices[i];}
            if(okA&&(!okB||ca<=cb)){mask|=a;cost+=ca;}else{mask|=b;cost+=cb;}
            remaining&=~component;components++;
        }
        return new(new(mask,checked((int)cost)),1,components,0,false,SelectionTicks);
    }
}
