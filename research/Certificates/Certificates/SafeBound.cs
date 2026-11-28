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

    public static BoundCertificate Compute(SparseModel m,Q[] y)
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
            var b=rj.Sign>0?m.ColLower[j]:m.ColUpper[j];
            if(b<0)return new(null,false,used,zeroed,j,"box-lagrangian/exact");
            bound+=rj*m.Pool[b];
        }
        if(m.ObjectiveOffset>=0)bound+=sign*m.Pool[m.ObjectiveOffset];
        // Integral objective: every costed column integer with integer cost, integer offset -> objective ∈ Z, round inward.
        bool integral=(m.ObjectiveOffset<0||m.Pool[m.ObjectiveOffset].IsInteger)&&
            Enumerable.Range(0,m.Columns).All(j=>m.Objective[j]<0||m.Integer[j]&&m.Pool[m.Objective[j]].IsInteger);
        if(integral&&!bound.IsInteger)bound=Q.Of(bound.Ceiling(),BigInteger.One);
        return new(sign*bound,integral,used,zeroed,null,"box-lagrangian/exact");
    }

    /// <summary>Best valid bound over several candidate multiplier vectors (e.g. y and −y when the solver's sign convention
    /// is unknown). Every candidate is valid on its own, so taking the tightest is valid.</summary>
    public static BoundCertificate Best(SparseModel m,params Q[][] candidates)
    {
        BoundCertificate? best=null;
        foreach(var y in candidates)
        {
            var c=Compute(m,y);
            if(c.Bound is null)continue;
            if(best?.Bound is not {} b||(m.Maximize?c.Bound.Value<b:c.Bound.Value>b))best=c;
        }
        return best??Compute(m,candidates[0]);
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
