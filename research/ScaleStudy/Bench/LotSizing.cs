using WowDi.Research.Sparse;

namespace ScaleBench;

/// <summary>Capacitated multi-item multi-period lot sizing with setups (a standard industrial production-planning model).
/// Columns per (p,t): production x (integer, [0,M]), inventory s (integer, [0,∞)), setup y (binary).
/// Rows: balance s_{p,t-1} + x_{p,t} - s_{p,t} = d_{p,t}; linking x - M y &lt;= 0; capacity per (r,t):
/// Σ_p a_{pr} x_{pt} + b_{pr} y_{pt} &lt;= C_{rt}. Nonzeros ≈ (5 + 2R) per (p,t).
/// A feasible point is known by construction (lot-for-lot: x = d, s = 0, y = [d &gt; 0]); capacities include slack.</summary>
public static class LotSizing
{
    public sealed record Instance(SparseModel Model,long[] Feasible,int Products,int Periods,int Resources,int BalanceRow0);

    public static Instance Build(int products,int periods,int resources,int seed)
    {
        var r=new Random(seed);
        var b=new SparseBuilder($"lotsizing-p{products}-t{periods}-r{resources}",false);
        int n=products*periods;
        var d=new long[products,periods];var a=new long[products,resources];var setup=new long[products,resources];
        for(int p=0;p<products;p++)
        {
            for(int t=0;t<periods;t++)d[p,t]=r.Next(4)==0?0:r.Next(1,50);
            for(int k=0;k<resources;k++){a[p,k]=r.Next(1,4);setup[p,k]=r.Next(1,10);}
        }
        const long M=10_000;
        int X(int p,int t)=>3*(p*periods+t);
        var feasible=new long[3*n];
        for(int p=0;p<products;p++)for(int t=0;t<periods;t++)
        {
            b.AddColumn(0,M,true,r.Next(1,5));            // x: unit production cost
            b.AddColumn(0,null,true,r.Next(1,3));         // s: holding cost
            b.AddColumn(0,1,true,r.Next(20,80));          // y: setup cost
            feasible[X(p,t)]=d[p,t];feasible[X(p,t)+2]=d[p,t]>0?1:0;
        }
        int balance0=b.Rows;
        for(int p=0;p<products;p++)for(int t=0;t<periods;t++)
        {
            if(t==0)b.AddRow([X(p,t),X(p,t)+1],[1L,-1L],d[p,t],d[p,t]);
            else b.AddRow([X(p,t-1)+1,X(p,t),X(p,t)+1],[1L,1L,-1L],d[p,t],d[p,t]);
            b.AddRow([X(p,t),X(p,t)+2],[1L,-M],null,0);
        }
        var cols=new int[2*products];var coef=new long[2*products];
        for(int t=0;t<periods;t++)for(int k=0;k<resources;k++)
        {
            long use=0;
            for(int p=0;p<products;p++)
            {
                cols[2*p]=X(p,t);coef[2*p]=a[p,k];cols[2*p+1]=X(p,t)+2;coef[2*p+1]=setup[p,k];
                use+=a[p,k]*d[p,t]+(d[p,t]>0?setup[p,k]:0);
            }
            b.AddRow(cols,coef,null,use+use/5+10);
        }
        return new(b.Build(),feasible,products,periods,resources,balance0);
    }

    /// <summary>Choose (products, periods) for a target number of nonzeros with R=2 (≈9 nnz per (p,t)), periods fixed at 20.</summary>
    public static (int P,int T) ForNonzeros(long target)=>((int)Math.Max(1,target/9/20),20);
}
