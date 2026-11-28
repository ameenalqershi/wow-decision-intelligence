using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;

namespace PortfolioStudy;

/// <summary>Neutral pure-integer linear instance: x_j in [0,U_j], rows sum a_ij x_j &lt;= b_i, linear objective.
/// Both engines receive exactly this data; the UDIR model for the independent verifier is compiled from the same arrays.</summary>
public sealed record IlpInstance(string Name,string Family,int Seed,int[] Upper,int[][] RowIdx,int[][] RowCoef,long[] Rhs,int[] Cost,bool Maximize)
{
    public int N=>Upper.Length;
    public int Nonzeros=>RowIdx.Sum(r=>r.Length)+Cost.Count(c=>c!=0);
}

public static class Generators
{
    public static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":[],"calendars":["shift"]}""");

    /// <summary>Multidimensional 0-1 knapsack, correlated profits (Chu &amp; Beasley style), tightness 0.5.</summary>
    public static IlpInstance Knapsack(int n,int m,int seed)
    {
        var r=new Random(seed);
        var w=Enumerable.Range(0,m).Select(_=>Enumerable.Range(0,n).Select(_=>r.Next(1,101)).ToArray()).ToArray();
        var p=Enumerable.Range(0,n).Select(j=>w.Sum(row=>row[j])/m+r.Next(1,21)).ToArray();
        var rows=w.Select(row=>Enumerable.Range(0,n).ToArray()).ToArray();
        return new($"mkp-n{n}-m{m}-s{seed}","knapsack",seed,Enumerable.Repeat(1,n).ToArray(),rows,w,w.Select(row=>(long)(row.Sum()/2)).ToArray(),p,true);
    }

    /// <summary>Generalized assignment (type D-like: cost inversely related to weight), minimize cost.
    /// x_aj binary, each job exactly once (two inequalities), agent capacity 0.8 * average load.</summary>
    public static IlpInstance Assignment(int agents,int jobs,int seed)
    {
        var r=new Random(seed);
        int V(int a,int j)=>a*jobs+j;
        var w=Enumerable.Range(0,agents).Select(_=>Enumerable.Range(0,jobs).Select(_=>r.Next(1,101)).ToArray()).ToArray();
        var cost=new int[agents*jobs];
        for(int a=0;a<agents;a++)for(int j=0;j<jobs;j++)cost[V(a,j)]=111-w[a][j]+r.Next(-10,11);
        var idx=new List<int[]>();var coef=new List<int[]>();var rhs=new List<long>();
        for(int j=0;j<jobs;j++)
        {
            var cols=Enumerable.Range(0,agents).Select(a=>V(a,j)).ToArray();
            idx.Add(cols);coef.Add(Enumerable.Repeat(1,agents).ToArray());rhs.Add(1);
            idx.Add(cols);coef.Add(Enumerable.Repeat(-1,agents).ToArray());rhs.Add(-1);
        }
        for(int a=0;a<agents;a++)
        {
            // Type-D style capacity: 0.9 * (agent's total weight / agents) + 20 (feasible-leaning, not guaranteed).
            idx.Add(Enumerable.Range(0,jobs).Select(j=>V(a,j)).ToArray());coef.Add(w[a]);rhs.Add((long)Math.Ceiling(0.9*w[a].Sum()/agents)+20);
        }
        return new($"gap-a{agents}-j{jobs}-s{seed}","assignment",seed,Enumerable.Repeat(1,agents*jobs).ToArray(),
            idx.ToArray(),coef.ToArray(),rhs.ToArray(),cost,false);
    }

    /// <summary>General-integer covering: minimize cost, sum a_ij x_j &gt;= d_i written as -sum &lt;= -d, x_j in [0,5], density 0.3.</summary>
    public static IlpInstance Covering(int n,int m,int seed)
    {
        var r=new Random(seed);
        var idx=new List<int[]>();var coef=new List<int[]>();var rhs=new List<long>();
        for(int i=0;i<m;i++)
        {
            var cols=Enumerable.Range(0,n).Where(_=>r.NextDouble()<0.3).ToList();
            if(cols.Count==0)cols.Add(r.Next(n));
            idx.Add(cols.ToArray());coef.Add(cols.Select(_=>-r.Next(1,5)).ToArray());rhs.Add(-r.Next(5,25));
        }
        return new($"cov-n{n}-m{m}-s{seed}","covering",seed,Enumerable.Repeat(5,n).ToArray(),idx.ToArray(),coef.ToArray(),rhs.ToArray(),
            Enumerable.Range(0,n).Select(_=>r.Next(1,21)).ToArray(),false);
    }

    /// <summary>Compile the same instance to UDIR through the production DML front end (no solver involved).</summary>
    /// <param name="balanced">Write each linear sum as a balanced tree (depth log2 n). The natural left-chained form
    /// "a + b + c ..." exceeds DmlParser.MaximumExpressionDepth (64) beyond ~64 terms; both forms are semantically identical.</param>
    public static (DmlCompilation? Compiled,string? Diagnostic,int DmlBytes) Compile(IlpInstance x,bool balanced=true)
    {
        static string Lit(long v)=>v<0?"0 - "+(-v).ToString(CultureInfo.InvariantCulture):v.ToString(CultureInfo.InvariantCulture);
        string Sum(IEnumerable<(int Col,int Coef)> terms)
        {
            var t=terms.Where(t=>t.Coef!=0).ToArray();
            if(t.Length==0)return "0";
            if(!balanced)
            {
                var sb=new StringBuilder();
                for(int k=0;k<t.Length;k++)
                {
                    var (j,c)=t[k];var mag=Math.Abs(c).ToString(CultureInfo.InvariantCulture)+"*x"+j.ToString(CultureInfo.InvariantCulture);
                    if(k==0)sb.Append(c<0?"0 - "+mag:mag);else sb.Append(c<0?" - ":" + ").Append(mag);
                }
                return sb.ToString();
            }
            string Leaf((int Col,int Coef) z)=>z.Coef<0?"(0 - "+(-z.Coef).ToString(CultureInfo.InvariantCulture)+"*x"+z.Col.ToString(CultureInfo.InvariantCulture)+")"
                :z.Coef.ToString(CultureInfo.InvariantCulture)+"*x"+z.Col.ToString(CultureInfo.InvariantCulture);
            string Tree(int lo,int hi)=>hi-lo==1?Leaf(t[lo]):"("+Tree(lo,(lo+hi)/2)+" + "+Tree((lo+hi)/2,hi)+")";
            return Tree(0,t.Length);
        }
        var text=new StringBuilder("problem P { ");
        for(int j=0;j<x.N;j++)text.Append(CultureInfo.InvariantCulture,$"decision x{j} : Int in [0,{x.Upper[j]}]; ");
        for(int i=0;i<x.Rhs.Length;i++)
            text.Append(CultureInfo.InvariantCulture,$"constraint r{i} : {Sum(x.RowIdx[i].Zip(x.RowCoef[i]).Select(z=>(z.First,z.Second)))} <= {Lit(x.Rhs[i])}; ");
        text.Append(x.Maximize?"maximize":"minimize").Append(" goal priority 1 : ").Append(Sum(x.Cost.Select((c,j)=>(j,c)))).Append("; }");
        var bytes=Encoding.UTF8.GetBytes(text.ToString());
        var bound=DmlBinder.Bind("src",bytes,new BindingContext());
        if(!bound.Accepted)return (null,bound.Diagnostic?.Code,bytes.Length);
        var compiled=DmlCompiler.Compile(bound.Document!,new("tenant","model-"+x.Name,"rev",new("shift","UTC","2026-01-01T00:00:00Z","minute"),
            new([new("minute","time","1")],[],["shift"]),[Convert.ToHexStringLower(SHA256.HashData(Catalog))],[],["integer.linear"]));
        return compiled.Accepted?(compiled.Compilation,null,bytes.Length):(null,compiled.Diagnostic?.Code,bytes.Length);
    }
}
