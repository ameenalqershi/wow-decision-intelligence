using System.Numerics;

namespace WowDi.Research.Sparse;

/// <summary>Result of an exact feasibility/objective evaluation. Violations are exact rationals (no tolerance applied);
/// a tolerance policy is a separate, explicit decision (<see cref="SparseVerifier.WithinTolerance"/>).</summary>
public sealed record SparseCheck(bool ExactFeasible,Q Objective,int ViolatedRows,int ViolatedColumns,int FirstViolatedRow,
    int FirstViolatedColumn,Q MaxRowViolation,Q MaxColumnViolation,long FastPathRows,long ExactPathRows);

/// <summary>Independent exact verifier for <see cref="SparseModel"/>. It never consults solver status, bounds or reported
/// objective values. Integer rows with an integer assignment use checked Int128 arithmetic; any overflow or fractional data
/// falls back to exact rationals. Rows are processed in fixed blocks and merged in block order, so the result is
/// deterministic for any degree of parallelism.</summary>
public static class SparseVerifier
{
    private const int Block=1<<15;

    public static SparseCheck Check(SparseModel m,long[] x,int parallelism=1)
    {
        if(x.Length!=m.Columns)throw new ArgumentException("assignment length");
        var small=SmallPool(m);
        return Run(m,parallelism,
            j=>CheckColumn(m,j,x[j]),
            i=>
            {
                int s=m.RowStart[i],e=m.RowStart[i+1];
                Int128 acc=0;bool fast=true;
                for(int k=s;k<e&&fast;k++)
                {
                    var a=small[m.ValueIndex[k]];
                    if(a is null){fast=false;break;}
                    try{acc=checked(acc+(Int128)a.Value*x[m.ColIndex[k]]);}catch(OverflowException){fast=false;}
                }
                if(fast)return (RowViolation(m,i,Q.Of((BigInteger)acc,BigInteger.One)),true);
                var q=Q.Zero;for(int k=s;k<e;k++)q+=m.Pool[m.ValueIndex[k]]*x[m.ColIndex[k]];
                return (RowViolation(m,i,q),false);
            },
            ()=>{var o=m.ObjectiveOffset>=0?m.Pool[m.ObjectiveOffset]:Q.Zero;for(int j=0;j<m.Columns;j++)if(m.Objective[j]>=0)o+=m.Pool[m.Objective[j]]*x[j];return o;});
    }

    public static SparseCheck Check(SparseModel m,Q[] x,int parallelism=1)
    {
        if(x.Length!=m.Columns)throw new ArgumentException("assignment length");
        return Run(m,parallelism,
            j=>CheckColumn(m,j,x[j]),
            i=>{var q=Q.Zero;for(int k=m.RowStart[i];k<m.RowStart[i+1];k++)q+=m.Pool[m.ValueIndex[k]]*x[m.ColIndex[k]];return (RowViolation(m,i,q),false);},
            ()=>{var o=m.ObjectiveOffset>=0?m.Pool[m.ObjectiveOffset]:Q.Zero;for(int j=0;j<m.Columns;j++)if(m.Objective[j]>=0)o+=m.Pool[m.Objective[j]]*x[j];return o;});
    }

    /// <summary>Exact evaluation of a floating solver point: every double converts to its exact dyadic rational.</summary>
    public static SparseCheck Check(SparseModel m,double[] x,int parallelism=1)
    {
        if(x.Length!=m.Columns)throw new ArgumentException("assignment length");
        if(x.All(v=>double.IsFinite(v)&&Math.Abs(v)<9e15&&v==Math.Floor(v)))return Check(m,x.Select(v=>(long)v).ToArray(),parallelism);
        return Check(m,x.Select(Q.FromDouble).ToArray(),parallelism);
    }

    /// <summary>Explicit tolerance policy: absolute violation ≤ tol (exact comparison). Integrality is never relaxed.</summary>
    public static bool WithinTolerance(SparseCheck c,Q tol)=>c.MaxRowViolation<=tol&&c.MaxColumnViolation<=tol;

    private static long?[] SmallPool(SparseModel m)
    {
        var a=new long?[m.Pool.Length];
        for(int k=0;k<a.Length;k++){var q=m.Pool[k];if(q.IsInteger&&q.N>=long.MinValue&&q.N<=long.MaxValue)a[k]=(long)q.N;}
        return a;
    }

    private static Q CheckColumn(SparseModel m,int j,Q v)
    {
        var viol=Q.Zero;
        if(m.ColLower[j]>=0&&v<m.Pool[m.ColLower[j]])viol=m.Pool[m.ColLower[j]]-v;
        if(m.ColUpper[j]>=0&&v>m.Pool[m.ColUpper[j]])viol=Q.Max(viol,v-m.Pool[m.ColUpper[j]]);
        if(m.Integer[j]&&!v.IsInteger)viol=Q.Max(viol,Q.Of(1,1)); // integrality: reported as a unit violation, never toleranced
        return viol;
    }
    private static Q RowViolation(SparseModel m,int i,Q act)
    {
        var viol=Q.Zero;
        if(m.RowLower[i]>=0&&act<m.Pool[m.RowLower[i]])viol=m.Pool[m.RowLower[i]]-act;
        if(m.RowUpper[i]>=0&&act>m.Pool[m.RowUpper[i]])viol=Q.Max(viol,act-m.Pool[m.RowUpper[i]]);
        return viol;
    }

    private sealed class Part{public int Bad,First=-1;public Q Max=Q.Zero;public long Fast,Exact;}

    private static SparseCheck Run(SparseModel m,int parallelism,Func<int,Q> column,Func<int,(Q Viol,bool Fast)> row,Func<Q> objective)
    {
        var colParts=Blocks(m.Columns,parallelism,(s,e,p)=>{for(int j=s;j<e;j++){var v=column(j);if(v.Sign>0){p.Bad++;if(p.First<0)p.First=j;if(v>p.Max)p.Max=v;}}});
        var rowParts=Blocks(m.Rows,parallelism,(s,e,p)=>{for(int i=s;i<e;i++){var (v,f)=row(i);if(f)p.Fast++;else p.Exact++;if(v.Sign>0){p.Bad++;if(p.First<0)p.First=i;if(v>p.Max)p.Max=v;}}});
        var obj=objective();
        Part Merge(Part[] parts){var r=new Part();foreach(var p in parts){r.Bad+=p.Bad;if(r.First<0)r.First=p.First;if(p.Max>r.Max)r.Max=p.Max;r.Fast+=p.Fast;r.Exact+=p.Exact;}return r;}
        var c=Merge(colParts);var rw=Merge(rowParts);
        return new(c.Bad==0&&rw.Bad==0,obj,rw.Bad,c.Bad,rw.First,c.First,rw.Max,c.Max,rw.Fast,rw.Exact);
    }

    private static Part[] Blocks(int count,int parallelism,Action<int,int,Part> body)
    {
        int blocks=(count+Block-1)/Block;var parts=new Part[blocks];
        Parallel.For(0,blocks,new ParallelOptions{MaxDegreeOfParallelism=Math.Max(1,parallelism)},b=>
        {var p=new Part();body(b*Block,Math.Min(count,(b+1)*Block),p);parts[b]=p;});
        return parts;
    }
}
