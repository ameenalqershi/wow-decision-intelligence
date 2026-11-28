using ComponentStudy;
using System.Numerics;
namespace AlgorithmDiscoveryLab;

// Exact Boolean cardinality propagation, with a current-model checked incumbent.
internal sealed class PropagationSearch : ICandidate
{
    private Optimum? previous;
    internal int Nodes { get; private set; }
    internal int Forced { get; private set; }
    internal int Contradictions { get; private set; }
    public Answer Solve(Model model)
    {
        int n=model.Size,u=(1<<n)-1;
        int[] c=Corpus.Prices(model);
        Optimum? best=previous is {} p&&Oracle.Evaluate(model,p.Mask) is int v?new(p.Mask,v):null;
        Nodes=Forced=Contradictions=0;int leaves=0;
        void Visit(int assigned,int mask)
        {
            Nodes++;
            bool changed;
            do
            {
                changed=false;
                foreach(var row in model.Capacities)
                {
                    int k=BitOperations.PopCount((uint)(mask&row.Scope));
                    int open=row.Scope&~assigned;
                    int rem=BitOperations.PopCount((uint)open);
                    if(k>row.Upper||k+rem<row.Lower){Contradictions++;return;}
                    if(open==0)continue;
                    if(k==row.Upper||k+rem==row.Lower)
                    {
                        if(k+rem==row.Lower)mask|=open;
                        assigned|=open;Forced+=rem;changed=true;
                    }
                }
            }while(changed);
            long floor=0;
            for(int i=0;i<n;i++)
                floor+=(assigned&(1<<i))==0?Math.Min(0,c[i]):(mask&(1<<i))!=0?c[i]:0;
            if(best is {} b&&floor>=b.Value)return;
            if(assigned==u){leaves++;best=new(mask,checked((int)floor));return;}
            int bit=(u&~assigned)&-(u&~assigned);
            int j=BitOperations.TrailingZeroCount((uint)bit);
            if(c[j]<0){Visit(assigned|bit,mask|bit);Visit(assigned|bit,mask);}
            else{Visit(assigned|bit,mask);Visit(assigned|bit,mask|bit);}
        }
        Visit(0,0);previous=best;
        return new(best,leaves,1,0,false,0);
    }
}
