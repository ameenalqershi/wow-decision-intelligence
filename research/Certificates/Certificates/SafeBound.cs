using System.Numerics;
using WowDi.Research.Sparse;

namespace WowDi.Research.Certificates;

/// <summary>Outcome of an exact bound computation. <see cref="Bound"/> is a valid bound on the optimal objective of the
/// LP relaxation (hence of the MILP) in the model's own sense: a lower bound when minimizing, an upper bound when
/// maximizing. Null means these multipliers certify no finite bound.</summary>
public sealed record BoundCertificate(Q? Bound,bool IntegerRounded,int MultipliersUsed,int MultipliersZeroed,int? UnboundedColumn,string Method);

public enum CertifiedStatus { ProvenOptimal, CertifiedGap, NoFiniteBound, Contradiction }

public sealed record GapAssessment(CertifiedStatus Status,Q? Bound,Q Incumbent,Q? AbsoluteGap,string Detail);

/// <summary>Exact box-Lagrangian bound (Neumaier &amp; Shcherbina 2004, "Safe bounds in linear and mixed-integer programming").
/// For minimization with rows lo ≤ a_i·x ≤ hi and boxes l ≤ x ≤ u, every multiplier vector y gives
///   c·x ≥ Σ_i y_i·b_i + Σ_j min_{x_j∈[l_j,u_j]} (c_j − (Aᵀy)_j)·x_j,   b_i = lo_i if y_i&gt;0, hi_i if y_i&lt;0,
/// whenever the needed bounds are finite (a multiplier whose bound is infinite is set to 0, which is still valid).
/// The inequality is weak duality, so it holds for ANY y: wrong, noisy or adversarial multipliers can only weaken the bound,
/// never make it invalid. All arithmetic is exact (doubles convert to their exact dyadic value). Nothing reported by the
/// solver about status, objective or gap is used.</summary>
public static class SafeBound
{
    public static BoundCertificate Compute(SparseModel m,ReadOnlySpan<double> y)
    {
        var q=new Q[y.Length];for(int i=0;i<y.Length;i++)q[i]=double.IsFinite(y[i])?Q.FromDouble(y[i]):Q.Zero;
        return Compute(m,q);
    }

    public static BoundCertificate Compute(SparseModel m,Q[] y)=>Compute(m,y,null);

    /// <summary>Exact implied column bounds by single-row activity propagation. Only infinite bounds are replaced, so the box
    /// stays a superset of the feasible set: every feasible x satisfies the implied bounds, hence the Lagrangian bound over this
    /// box remains valid. Integer columns are rounded inward. Overflowing rationals are skipped (bound stays infinite).</summary>
    public static (Q?[] Lower,Q?[] Upper) ImpliedBounds(SparseModel m,int passes=2)
    {
        var lo=new Q?[m.Columns];var hi=new Q?[m.Columns];
        for(int j=0;j<m.Columns;j++){lo[j]=m.ColLower[j]>=0?m.Pool[m.ColLower[j]]:null;hi[j]=m.ColUpper[j]>=0?m.Pool[m.ColUpper[j]]:null;}
        for(int pass=0;pass<passes;pass++)
        {
            bool changed=false;
            for(int i=0;i<m.Rows;i++)
            {
                int s=m.RowStart[i],e=m.RowStart[i+1];
                Q minAct=Q.Zero,maxAct=Q.Zero;int infMin=0,infMax=0;
                try
                {
                    for(int k=s;k<e;k++)
                    {
                        var a=m.Pool[m.ValueIndex[k]];var j=m.ColIndex[k];
                        Q? cmin=a.Sign>0?(lo[j] is {} l?a*l:null):(hi[j] is {} u?a*u:null);
                        Q? cmax=a.Sign>0?(hi[j] is {} u2?a*u2:null):(lo[j] is {} l2?a*l2:null);
                        if(cmin is {} x)minAct+=x;else infMin++;
                        if(cmax is {} y2)maxAct+=y2;else infMax++;
                    }
                    for(int k=s;k<e;k++)
                    {
                        var a=m.Pool[m.ValueIndex[k]];var j=m.ColIndex[k];
                        Q? cmin=a.Sign>0?(lo[j] is {} l?a*l:null):(hi[j] is {} u?a*u:null);
                        Q? cmax=a.Sign>0?(hi[j] is {} u2?a*u2:null):(lo[j] is {} l2?a*l2:null);
                        Q? restMin=infMin==0?minAct-cmin!.Value:(infMin==1&&cmin is null?minAct:null);
                        Q? restMax=infMax==0?maxAct-cmax!.Value:(infMax==1&&cmax is null?maxAct:null);
                        Q? newHi=null,newLo=null;
                        if(a.Sign>0)
                        {
                            if(m.RowUpper[i]>=0&&restMin is {} rm)newHi=(m.Pool[m.RowUpper[i]]-rm)/a;
                            if(m.RowLower[i]>=0&&restMax is {} rx)newLo=(m.Pool[m.RowLower[i]]-rx)/a;
                        }
                        else
                        {
                            if(m.RowLower[i]>=0&&restMax is {} rx)newHi=(m.Pool[m.RowLower[i]]-rx)/a;
                            if(m.RowUpper[i]>=0&&restMin is {} rm)newLo=(m.Pool[m.RowUpper[i]]-rm)/a;
                        }
                        if(hi[j] is null&&newHi is {} h){hi[j]=m.Integer[j]?Q.Of(h.Floor(),BigInteger.One):h;changed=true;}
                        if(lo[j] is null&&newLo is {} l3){lo[j]=m.Integer[j]?Q.Of(l3.Ceiling(),BigInteger.One):l3;changed=true;}
                    }
                }
                catch(OverflowException){}
            }
            if(!changed)break;
        }
        return (lo,hi);
    }

    public static BoundCertificate Compute(SparseModel m,Q[] y,(Q?[] Lower,Q?[] Upper)? box)
    {
        if(y.Length!=m.Rows)throw new ArgumentException("multiplier length");
        // Work in minimization form: min s·c·x with s = -1 for maximization.
        var sign=m.Maximize?-Q.One:Q.One;
        var r=new Q[m.Columns];
        for(int j=0;j<m.Columns;j++)r[j]=m.Objective[j]>=0?sign*m.Pool[m.Objective[j]]:Q.Zero;
        var bound=Q.Zero;int used=0,zeroed=0;
        for(int i=0;i<m.Rows;i++)
        {
            var yi=y[i];
            if(yi.Sign==0)continue;
            var b=yi.Sign>0?m.RowLower[i]:m.RowUpper[i];
            if(b<0){zeroed++;continue;}
            used++;
            bound+=yi*m.Pool[b];
            for(int k=m.RowStart[i];k<m.RowStart[i+1];k++)r[m.ColIndex[k]]-=yi*m.Pool[m.ValueIndex[k]];
        }
        for(int j=0;j<m.Columns;j++)
        {
            var rj=r[j];
            if(rj.Sign==0)continue;
            Q? colBound=box is {} bx?(rj.Sign>0?bx.Lower[j]:bx.Upper[j]):(rj.Sign>0?(m.ColLower[j]>=0?m.Pool[m.ColLower[j]]:null):(m.ColUpper[j]>=0?m.Pool[m.ColUpper[j]]:null));
            if(colBound is not {} cb)return new(null,false,used,zeroed,j,"box-lagrangian/exact");
            bound+=rj*cb;
        }
        if(m.ObjectiveOffset>=0)bound+=sign*m.Pool[m.ObjectiveOffset];
        // Integral objective: every costed column integer with integer cost, integer offset -> objective ∈ Z, round inward.
        bool integral=(m.ObjectiveOffset<0||m.Pool[m.ObjectiveOffset].IsInteger)&&
            Enumerable.Range(0,m.Columns).All(j=>m.Objective[j]<0||m.Integer[j]&&m.Pool[m.Objective[j]].IsInteger);
        if(integral&&!bound.IsInteger)bound=Q.Of(bound.Ceiling(),BigInteger.One);
        return new(sign*bound,integral,used,zeroed,null,box is null?"box-lagrangian/exact":"box-lagrangian/exact+implied-bounds");
    }

    /// <summary>Best valid bound over several candidate multiplier vectors (e.g. y and −y when the solver's sign convention
    /// is unknown). Every candidate is valid on its own, so taking the tightest is valid.</summary>
    public static BoundCertificate Best(SparseModel m,params Q[][] candidates)=>Best(m,null,candidates);
    public static BoundCertificate Best(SparseModel m,(Q?[] Lower,Q?[] Upper)? box,params Q[][] candidates)
    {
        BoundCertificate? best=null;
        foreach(var y in candidates)
        {
            BoundCertificate c;
            try{c=Compute(m,y,box);}
            catch(OverflowException){continue;}   // bounded arithmetic exceeded: this candidate certifies nothing

            if(c.Bound is null)continue;
            if(best?.Bound is not {} b||(m.Maximize?c.Bound.Value<b:c.Bound.Value>b))best=c;
        }
        return best??new BoundCertificate(null,false,0,0,null,"box-lagrangian/exact:no-finite-candidate");
    }

    /// <summary>Floating duals carry rounding noise (~1e-16) that can leave a reduced cost with the wrong sign on a column
    /// with an infinite bound, destroying the bound. Because ANY multiplier vector is valid, we may try cleaned candidates:
    /// both signs of the raw vector, dyadic roundings to 2^-k and continued-fraction approximations with small denominators
    /// (true duals of integer-data models are usually simple rationals). The tightest finite bound wins; validity is never
    /// at stake, only tightness. Cost: O(candidates x nnz) exact work.</summary>
    public static BoundCertificate Cleaned(SparseModel m,ReadOnlySpan<double> y)
    {
        var raw=new Q[y.Length];for(int i=0;i<y.Length;i++)raw[i]=double.IsFinite(y[i])?Q.FromDouble(y[i]):Q.Zero;
        var candidates=new List<Q[]>{raw};
        foreach(var k in new[]{0,8,16,24,32})candidates.Add(raw.Select(v=>Dyadic(v,k)).ToArray());
        foreach(var maxDen in new long[]{16,1024,1<<20})candidates.Add(raw.Select(v=>Simple(v,maxDen)).ToArray());
        var all=candidates.Concat(candidates.Select(c=>c.Select(v=>-v).ToArray())).ToArray();
        var best=Best(m,null,all);
        if(best.Bound is null)best=Best(m,ImpliedBounds(m,2),all);  // declared box failed: retry over the exact implied box
        if(best.Bound is null)best=Best(m,ImpliedBounds(m,8),all);
        return best with{Method=best.Method+"+cleaned"};
    }
    private static Q Dyadic(Q v,int bits)
    {
        var scale=BigInteger.One<<bits;var n=v.N*scale;var q=BigInteger.Divide(n+(n.Sign>=0?v.D/2:-(v.D/2)),v.D);
        return Q.Of(q,scale);
    }
    /// <summary>Best rational approximation with denominator ≤ maxDen (continued fractions).</summary>
    private static Q Simple(Q v,long maxDen)
    {
        if(v.Sign==0)return Q.Zero;
        BigInteger h0=0,h1=1,k0=1,k1=0,n=v.N,d=v.D;
        while(!d.IsZero)
        {
            var a=BigInteger.Divide(n,d);if(n.Sign<0&&!(a*d).Equals(n))a-=1;
            var h2=a*h1+h0;var k2=a*k1+k0;
            if(k2>maxDen)break;
            h0=h1;h1=h2;k0=k1;k1=k2;
            var r=n-a*d;n=d;d=r;
        }
        return k1.IsZero?Q.Zero:Q.Of(h1,k1);
    }

    /// <summary>Combine an independently verified incumbent value with a certified bound.</summary>
    public static GapAssessment Assess(SparseModel m,Q incumbent,BoundCertificate c)
    {
        if(c.Bound is not {} b)return new(CertifiedStatus.NoFiniteBound,null,incumbent,null,$"unbounded column {c.UnboundedColumn}");
        var gap=m.Maximize?b-incumbent:incumbent-b;
        if(gap.Sign<0)return new(CertifiedStatus.Contradiction,b,incumbent,gap,"verified incumbent beats a valid bound: model, data or verifier defect");
        return new(gap.Sign==0?CertifiedStatus.ProvenOptimal:CertifiedStatus.CertifiedGap,b,incumbent,gap,c.Method+(c.IntegerRounded?"+integer-rounding":""));
    }
}
