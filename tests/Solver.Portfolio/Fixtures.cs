using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Binding;
using WowDi.Dml.Lowering;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Udir;

namespace WowDi.Solver.Portfolio.Tests;

/// <summary>A small integer linear program with its own brute-force oracle (independent of S05 code paths).</summary>
public sealed class Ilp
{
    public required int[] Upper;            // x_j in [0, Upper_j]
    public required int[][] A;              // rows: sum_j A[i][j] x_j <= B[i]
    public required int[] B;
    public required int[] C;                // objective coefficients
    public required bool Maximize;
    public required DmlCompilation Compiled;
    public required byte[] Facts;
    public int N=>Upper.Length;
    public string Var(int j)=>Compiled.Original.Model.Symbols.Single(s=>s.Role==SymbolRole.Variable&&s.Id.EndsWith("x"+j,StringComparison.Ordinal)).Id;
    public string ObjectiveId=>Compiled.Original.Model.Objectives[0].Id;

    public bool Feasible(int[] x)
    {
        for(int j=0;j<N;j++)if(x[j]<0||x[j]>Upper[j])return false;
        for(int i=0;i<A.Length;i++){long s=0;for(int j=0;j<N;j++)s+=(long)A[i][j]*x[j];if(s>B[i])return false;}
        return true;
    }
    public long Value(int[] x){long s=0;for(int j=0;j<N;j++)s+=(long)C[j]*x[j];return s;}
    public IEnumerable<int[]> All()
    {
        var x=new int[N];
        while(true)
        {
            yield return (int[])x.Clone();
            int k=0;
            while(k<N&&x[k]==Upper[k]){x[k]=0;k++;}
            if(k==N)yield break;
            x[k]++;
        }
    }
    /// <summary>Exact optimum and one optimal and one non-optimal feasible point, by exhaustive enumeration.</summary>
    public (long? Optimum,int[]? Best,int[]? Worse) Oracle()
    {
        long? best=null;int[]? arg=null,worse=null;
        foreach(var x in All())
        {
            if(!Feasible(x))continue;
            var v=Value(x);
            if(best is null||(Maximize?v>best:v<best)){if(arg is not null)worse=arg;best=v;arg=x;}
            else if(v!=best)worse??=x;
        }
        return (best,arg,worse);
    }
    public int[]? Infeasible()
    {
        foreach(var x in All())if(!Feasible(x))return x;
        var y=new int[N];y[0]=Upper[0]+1;return y; // outside the domain
    }
}

public static class Fixtures
{
    public static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":[],"calendars":["shift"]}""");

    public static Ilp Random(Random r,bool forceInfeasible=false,int maxVars=3,int maxUpper=4)
    {
        int n=r.Next(1,maxVars+1),m=r.Next(0,3);
        var upper=Enumerable.Range(0,n).Select(_=>r.Next(1,maxUpper+1)).ToArray();
        var a=Enumerable.Range(0,m).Select(_=>Enumerable.Range(0,n).Select(_=>r.Next(-2,5)).ToArray()).ToArray();
        foreach(var row in a)if(row.All(v=>v==0))row[r.Next(n)]=1;
        var b=Enumerable.Range(0,m).Select(_=>r.Next(0,12)).ToArray();
        var c=Enumerable.Range(0,n).Select(_=>r.Next(-5,6)).ToArray();
        if(c.All(v=>v==0))c[0]=1;
        if(forceInfeasible)
        {   // sum x_j >= total upper + 1  <=>  -sum x_j <= -(U+1)
            a=[..a,Enumerable.Repeat(-1,n).ToArray()];b=[..b,-(upper.Sum()+1)];
        }
        return Build(upper,a,b,c,r.Next(2)==0);
    }

    public static Ilp Build(int[] upper,int[][] a,int[] b,int[] c,bool maximize)
    {
        static string Term(int coef,int j)=>coef.ToString(CultureInfo.InvariantCulture)+"*x"+j;
        static string Sum(int[] coefs)
        {
            var parts=coefs.Select((v,j)=>(v,j)).Where(t=>t.v!=0).ToArray();
            var sb=new StringBuilder();
            for(int k=0;k<parts.Length;k++)
            {
                var (v,j)=parts[k];
                if(k==0)sb.Append(v<0?"0 - "+Term(-v,j):Term(v,j));
                else sb.Append(v<0?" - ":" + ").Append(Term(Math.Abs(v),j));
            }
            return sb.ToString();
        }
        var text=new StringBuilder("problem P { ");
        for(int j=0;j<upper.Length;j++)text.Append(CultureInfo.InvariantCulture,$"decision x{j} : Int in [0,{upper[j]}]; ");
        for(int i=0;i<a.Length;i++)
        {
            var rhs=b[i]<0?"0 - "+(-b[i]).ToString(CultureInfo.InvariantCulture):b[i].ToString(CultureInfo.InvariantCulture);
            text.Append(CultureInfo.InvariantCulture,$"constraint r{i} : {Sum(a[i])} <= {rhs}; ");
        }
        text.Append(maximize?"maximize":"minimize").Append(" goal priority 1 : ").Append(Sum(c)).Append("; }");
        var bound=DmlBinder.Bind("src",Encoding.UTF8.GetBytes(text.ToString()),new BindingContext());
        if(!bound.Accepted)throw new InvalidOperationException(bound.Diagnostic?.Message+" :: "+text);
        var compiled=DmlCompiler.Compile(bound.Document!,new("tenant","model","rev",new("shift","UTC","2026-01-01T00:00:00Z","minute"),
            new([new("minute","time","1")],[],["shift"]),[Convert.ToHexStringLower(SHA256.HashData(Catalog))],[],["integer.linear"]));
        if(!compiled.Accepted)throw new InvalidOperationException(compiled.Diagnostic?.Message+" :: "+text);
        var c2=compiled.Compilation!;
        return new Ilp{Upper=upper,A=a,B=b,C=c,Maximize=maximize,Compiled=c2,Facts=c2.GeneratedInputs[c2.FactsDigest].ToArray()};
    }

    public static AdapterProfile Profile(string id,SolverFamily family,int maxThreads=4,int minThreads=1,long minMemory=1024,
        TimeSpan? minDeadline=null,string[]? features=null)=>new(
        new(new(id,"1","test-rid"),features?.ToImmutableArray()??(family==SolverFamily.CpSat?["integer.linear"]:["integer.linear","rational.linear"]),
            family==SolverFamily.CpSat?[ScalarKind.Integer,ScalarKind.Boolean]:[ScalarKind.Integer,ScalarKind.Boolean,ScalarKind.Rational],
            null,null,maxThreads,false,true,true),family,family==SolverFamily.CpSat?NumericEncoding.Int64:NumericEncoding.Binary64ExactInput,
        new string('a',64),minThreads,minMemory,minDeadline??TimeSpan.FromMilliseconds(1));

    public static PortfolioRequest Request(Ilp ilp,PortfolioBudget budget,PortfolioPolicy? policy=null,string run="run")=>
        new(run,ilp.Compiled.Normalized,Catalog,ilp.Facts,"test-rid",NumericRequirement.ExactInput,budget,policy??new());

    public static SolverResult Result(Ilp ilp,MemberAssignment a,SolverIdentity solver,int[]? x,MathematicalStatus status,
        string? reportedValue=null,string? bound=null,Termination termination=Termination.Completed)
    {
        var m=ilp.Compiled.Original.Model;
        var inc=x is null?null:Enumerable.Range(0,ilp.N).ToImmutableDictionary(j=>ilp.Var(j),j=>x[j].ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal);
        var value=x is null?null:reportedValue??ilp.Value(x).ToString(CultureInfo.InvariantCulture);
        var dir=ilp.Maximize?Direction.Maximize:Direction.Minimize;
        var evidence=new Evidence(EvidenceSource.SolverAttestation,"sim");
        return new(m.TenantId,m.ModelId,m.RevisionId,a.RunId,solver,status,termination,inc,
            [new(ilp.ObjectiveId,value,bound is null?null:new(bound,dir,evidence),"0","0")],
            status is MathematicalStatus.Optimal or MathematicalStatus.Infeasible or MathematicalStatus.Unbounded?evidence:null,new(null,null,null));
    }
}
