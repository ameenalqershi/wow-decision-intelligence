using ComponentStudy;
using System.Numerics;
namespace AlgorithmDiscoveryLab;

// G10: exact pair quotient with signed cardinality interval propagation.
// Every pruning rule is necessary, never a sufficient feasibility test.
internal sealed class CompressedPropagation(bool propagate) : ICandidate
{
    private Optimum? previous;
    internal string Route {get;private set;}="";
    internal int Nodes {get;private set;}
    internal int Blocks {get;private set;}
    internal int Forced {get;private set;}
    internal bool HintAccepted {get;private set;}
    public Answer Solve(Model m)
    {
        int n=m.Size,u=(1<<n)-1,zeros=0,ones=0;
        Nodes=Blocks=Forced=0;HintAccepted=false;Route="quotient";
        Span<int> adj=stackalloc int[n];adj.Clear();
        var general=new List<Capacity>();
        foreach(var r in m.Capacities)
        {
            int w=BitOperations.PopCount((uint)r.Scope);
            if(r.Lower==0&&r.Upper==w)continue;
            if(r.Upper==0){zeros|=r.Scope;continue;}
            if(r.Lower==w){ones|=r.Scope;continue;}
            if(w==2&&r.Lower==1&&r.Upper==1)
            {
                int a=BitOperations.TrailingZeroCount((uint)r.Scope),b=BitOperations.Log2((uint)r.Scope);
                adj[a]|=1<<b;adj[b]|=1<<a;
            }
            else general.Add(r);
        }
        Answer Infeasible(){Route="contradiction";previous=null;return new(null,0,Blocks,0,false,0);}
        if((zeros&ones)!=0)return Infeasible();
        int[] c=Corpus.Prices(m);
        long Price(int mask){long sum=0;while(mask!=0){int bit=mask&-mask;mask^=bit;sum+=c[BitOperations.TrailingZeroCount((uint)bit)];}return sum;}
        var blocks=new List<(int A,int B,long CA,long CB)>();
        int remaining=u,fixedMask=0;long fixedCost=0;
        while(remaining!=0)
        {
            int a=remaining&-remaining,b=0,front=a;
            while(front!=0)
            {
                int next=0,scan=front;
                while(scan!=0){int bit=scan&-scan;scan^=bit;next|=adj[BitOperations.TrailingZeroCount((uint)bit)];}
                if((front&a)!=0){if((next&a)!=0)return Infeasible();next&=~b;b|=next;}
                else{if((next&b)!=0)return Infeasible();next&=~a;a|=next;}
                front=next;
            }
            remaining&=~(a|b);
            bool okA=(a&zeros)==0&&(b&ones)==0,okB=(b&zeros)==0&&(a&ones)==0;
            if(!okA&&!okB)return Infeasible();
            if(okA&&okB)blocks.Add((a,b,Price(a),Price(b)));
            else {int bitmask=okA?a:b;fixedMask|=bitmask;fixedCost+=Price(bitmask);}
        }
        int k=Blocks=blocks.Count;
        var rows=new List<(int Base,int L,int U,int[] D)>();
        foreach(var r in general)
        {
            int baseline=BitOperations.PopCount((uint)(fixedMask&r.Scope)),low=0,high=0;
            int[] d=new int[k];
            for(int j=0;j<k;j++)
            {
                int ca=BitOperations.PopCount((uint)(blocks[j].A&r.Scope));
                int cb=BitOperations.PopCount((uint)(blocks[j].B&r.Scope));
                baseline+=ca;d[j]=cb-ca;low+=Math.Min(0,d[j]);high+=Math.Max(0,d[j]);
            }
            if(baseline+low>r.Upper||baseline+high<r.Lower)return Infeasible();
            if(baseline+low>=r.Lower&&baseline+high<=r.Upper)continue;
            rows.Add((baseline,r.Lower,r.Upper,d));
        }
        if(rows.Count==0)
        {
            Route="direct";int mask=fixedMask;long sum=fixedCost;
            foreach(var b in blocks){if(b.CA<=b.CB){mask|=b.A;sum+=b.CA;}else{mask|=b.B;sum+=b.CB;}}
            previous=new(mask,checked((int)sum));return new(previous,1,k,0,false,0);
        }
        Optimum? best=previous is {} p&&Oracle.Evaluate(m,p.Mask) is int v?new(p.Mask,v):null;
        HintAccepted=best!=null;
        int universe=(1<<k)-1,leaves=0;
        void Visit(int assigned,int chosen)
        {
            Nodes++;
            bool changed;
            do
            {
                changed=false;
                foreach(var row in rows)
                {
                    int lo=row.Base,hi=row.Base;
                    for(int j=0;j<k;j++)
                    {
                        int bit=1<<j,d=row.D[j];
                        if((assigned&bit)!=0){if((chosen&bit)!=0){lo+=d;hi+=d;}}
                        else{lo+=Math.Min(0,d);hi+=Math.Max(0,d);}
                    }
                    if(lo>row.U||hi<row.L)return;
                    if(!propagate)continue;
                    for(int j=0;j<k;j++)
                    {
                        int bit=1<<j,d=row.D[j];
                        if((assigned&bit)!=0||d==0)continue;
                        // Remove current variable's interval, then test each exact choice.
                        int otherLo=lo-Math.Min(0,d),otherHi=hi-Math.Max(0,d);
                        bool a=otherLo<=row.U&&otherHi>=row.L;
                        bool b=otherLo+d<=row.U&&otherHi+d>=row.L;
                        if(!a&&!b)return;
                        if(a==b)continue;
                        assigned|=bit;if(b)chosen|=bit;
                        lo=otherLo+(b?d:0);hi=otherHi+(b?d:0);
                        changed=true;Forced++;
                    }
                }
            }while(changed);
            long floor=fixedCost;
            for(int j=0;j<k;j++)
            {
                var b=blocks[j];int bit=1<<j;
                floor+=(assigned&bit)==0?Math.Min(b.CA,b.CB):(chosen&bit)==0?b.CA:b.CB;
            }
            if(best is {} incumbent&&floor>=incumbent.Value)return;
            if(assigned==universe)
            {
                int mask=fixedMask;
                for(int j=0;j<k;j++)mask|=(chosen&(1<<j))==0?blocks[j].A:blocks[j].B;
                leaves++;best=new(mask,checked((int)floor));return;
            }
            int next=(universe&~assigned)&-(universe&~assigned),idx=BitOperations.TrailingZeroCount((uint)next);
            if(blocks[idx].CA<=blocks[idx].CB){Visit(assigned|next,chosen);Visit(assigned|next,chosen|next);}
            else{Visit(assigned|next,chosen|next);Visit(assigned|next,chosen);}
        }
        Visit(0,0);previous=best;
        return new(best,leaves,k,0,false,0);
    }
}
