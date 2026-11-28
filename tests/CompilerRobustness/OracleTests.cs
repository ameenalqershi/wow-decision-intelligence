using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.Dml.Lowering;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;
using WowDi.Verification.Bounds;
using Xunit;

namespace WowDi.CompilerRobustness.Tests;

public sealed class OracleTests
{
    private static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":[],"calendars":["shift"]}""");
    private static readonly MeasurementSemantics Measurements=new([new("minute","time","1")],[],["shift"]);
    private static readonly SolverIdentity Identity=new("oracle-fixture","1","managed");
    private static readonly SolverCapabilities Manifest=new(Identity,["integer.linear"],[ScalarKind.Integer,ScalarKind.Rational],null,null,1,false,true,true);
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    private static string I(int n)=>n.ToString(CultureInfo.InvariantCulture);
    // Objective always has denominator 3. Reduction uses only integer divisibility,
    // never production rational arithmetic, normalization, or native mapping.
    private static string Third(int n)=>n%3==0?I(n/3):I(n)+"/3";
    private static DmlCompileOptions Options()=>new("tenant","model","rev",new("shift","UTC","2026-01-01T00:00:00Z","minute"),Measurements,[Hash(Catalog)],[],["integer.linear"]);
    private sealed record Case(int A,int B,int R,int Floor,int P,int Q,int Offset,Direction Direction)
    {
        public bool Feasible(int x,int y)=>A*x+B*y<=R&&x+y>=Floor;
        public int Numerator(int x,int y)=>P*x+Q*y+Offset;
        public string Source=>FormattableString.Invariant($"problem P {{ decision x : Int in [-2,2]; decision y : Int in [-2,2]; constraint cap : ({A}*x + {B}*y)/3 <= {R}/3; constraint floor : x+y >= {Floor}; {(Direction==Direction.Minimize?"minimize":"maximize")} goal priority 1 : ({P}*x+{Q}*y+{Offset})/3; }}");
    }
    private static DmlCompilation Compile(string source)
    {
        var bound=DmlBinder.Bind("generated",Encoding.UTF8.GetBytes(source),new BindingContext());
        Assert.True(bound.Accepted,bound.Diagnostic?.Message);
        var c=DmlCompiler.Compile(bound.Document!,Options());
        Assert.True(c.Accepted,c.Diagnostic?.Code+": "+c.Diagnostic?.Message);
        return c.Compilation!;
    }
    private static SolverResult Result(EvidenceBinding b, int? x=null,int? y=null,string? value=null)
        =>new(b.Scope.TenantId,b.Scope.ModelId,b.Scope.RevisionId,b.Scope.RunId,Identity,
            x is null?MathematicalStatus.Unknown:MathematicalStatus.Feasible,Termination.Timeout,
            x is null?null:ImmutableDictionary<string,string>.Empty.Add("x",I(x.Value)).Add("y",I(y!.Value)),
            [new("goal",value,null,"0","0")],null,new(null,null,null));

    // Each seed is independently replayable through the theory test case name.
    // Explicit fixed seeds are retained even if a failure disappears after correction.
    public static IEnumerable<object[]> Seeds()=>Enumerable.Range(0,64).Select(i=>new object[]{i});
    [Theory,MemberData(nameof(Seeds))]
    public void ExhaustiveSourceModelAndProofAgree(int seed)
    {
        var random=new Random(seed);
        var f=new Case(random.Next(-3,4),random.Next(-3,4),random.Next(-5,6),random.Next(-5,6),
            random.Next(-3,4),random.Next(-3,4),random.Next(-4,5),seed%2==0?Direction.Minimize:Direction.Maximize);
        var c=Compile(f.Source);
        var doc=c.Original;
        var facts=c.GeneratedInputs[c.FactsDigest].ToArray();
        var binding=new EvidenceBinding(new("tenant","model","rev","run",Hash(doc.Write())),Hash(facts),Hash(Catalog));
        var lower=ExactLowerer.Lower(c.Normalized,Manifest,NumericEncoding.Int64,facts);
        Assert.True(lower.Accepted,lower.Diagnostic?.Message);
        var model=lower.Model!;
        var feasible=new List<(int X,int Y,int Numerator)>();
        for(int x=-2;x<=2;x++)for(int y=-2;y<=2;y++)
        {
            var expected=f.Feasible(x,y);
            var value=f.Numerator(x,y);
            var candidate=Result(binding,x,y,Third(value));
            var report=OriginalVerifier.Check(doc,Measurements,binding.Scope,candidate,facts);
            Assert.Equal(expected,report.Accepted);
            Assert.Equal(Third(value),Assert.Single(report.Objectives).ExactValue);
            Assert.Equal(expected,model.Rows.All(row=>
            {
                var lhs=row.Terms.Sum(t=>long.Parse(t.Value,CultureInfo.InvariantCulture)*(t.Column==0?x:y));
                var rhs=long.Parse(row.RightHandSide,CultureInfo.InvariantCulture);
                return row.Relation switch{Operation.Equal=>lhs==rhs,Operation.LessOrEqual=>lhs<=rhs,Operation.GreaterOrEqual=>lhs>=rhs,_=>throw new InvalidOperationException()};
            }));
            var goal=model.Objective!;
            var native=goal.Terms.Sum(t=>long.Parse(t.Value,CultureInfo.InvariantCulture)*(t.Column==0?x:y))+long.Parse(goal.Offset,CultureInfo.InvariantCulture);
            // Cross-multiply; do not use RestoreObjective or ExactValues as oracle.
            Assert.Equal((long)value*long.Parse(goal.Scale,CultureInfo.InvariantCulture),native*3);
            if(expected)
            {
                feasible.Add((x,y,value));
                var forged=candidate with{Objectives=[candidate.Objectives[0] with{IncumbentValue=Third(value+1)}]};
                Assert.False(OriginalVerifier.Check(doc,Measurements,binding.Scope,forged,facts).Accepted);
            }
        }
        var best=feasible.Count==0?default:f.Direction==Direction.Minimize?feasible.MinBy(p=>p.Numerator):feasible.MaxBy(p=>p.Numerator);
        var result=feasible.Count==0?Result(binding):Result(binding,best.X,best.Y,Third(best.Numerator));
        BoundAssessment Assess(SolverResult r,bool enumerate=true,int budget=4096,EvidenceBinding? observation=null)=>
            EvidenceClassifier.Assess(doc,binding,Catalog,facts,new(observation??binding,r),Manifest,enumerate,budget);
        var proof=Assess(result);
        Assert.False(proof.Rejected,proof.Diagnostic);
        Assert.Equal(25,proof.Proof!.Checked);
        Assert.Equal(feasible.Count,proof.Proof.Feasible);
        Assert.Equal(feasible.Count==0?MathematicalStatus.Infeasible:MathematicalStatus.Optimal,proof.Status);
        Assert.Equal(feasible.Count==0?null:Third(best.Numerator),proof.Proof.Optimum);
        Assert.Equal(EvidenceStrength.ExactEnumeration,proof.Strength);
        if(feasible.Count>0)
        {
            Assert.Equal("0",proof.AbsoluteGap);
            var stale=result with{RunId="stale"};
            Assert.True(Assess(stale).Rejected);
            var wrongDirection=f.Direction==Direction.Minimize?Direction.Maximize:Direction.Minimize;
            var row=result.Objectives[0];
            var invalid=row with{Bound=new(Third(best.Numerator),wrongDirection,new(EvidenceSource.SolverAttestation,"wrong-direction"))};
            Assert.True(Assess(result with{Objectives=[invalid]}).Rejected);
            var wrongBound=row with{Bound=new(Third(best.Numerator+(f.Direction==Direction.Minimize?1:-1)),f.Direction,new(EvidenceSource.SolverAttestation,"contradiction"))};
            Assert.True(Assess(result with{Objectives=[wrongBound]}).Rejected);
            var forgedCert=row with{Bound=new(Third(best.Numerator),f.Direction,new(EvidenceSource.IndependentCertificate,"forged"))};
            Assert.True(Assess(result with{Objectives=[forgedCert]}).Rejected);
        }
        var incomplete=Assess(result,budget:24);
        Assert.Null(incomplete.Proof);
        Assert.Equal(feasible.Count==0?MathematicalStatus.Unknown:MathematicalStatus.Feasible,incomplete.Status);
        Assert.True(Assess(result,observation:binding with{CatalogSha256=new string('0',64)}).Rejected);
    }

    // Fixed minimal negative source corpus: each case is reproduced on every run.
    [Theory]
    [InlineData("decision x : Int in [0,1]; constraint c : ;")]
    [InlineData("decision x : Int in [0,1]; decision x : Int in [0,1];")]
    [InlineData("decision x : Int in [0,1]; minimize goal priority 1 : missing;")]
    [InlineData("decision x : Int in [0,1]; minimize goal priority 1 : x/0;")]
    public void InvalidSourceHasDiagnosticAndNoCompilation(string body)
    {
        var bound=DmlBinder.Bind("negative",Encoding.UTF8.GetBytes("problem P { "+body+" }"),new BindingContext());
        if(!bound.Accepted){Assert.NotNull(bound.Diagnostic);Assert.Null(bound.Document);return;}
        var result=DmlCompiler.Compile(bound.Document!,Options());
        Assert.False(result.Accepted);
        Assert.NotNull(result.Diagnostic);
        Assert.Null(result.Compilation);
    }
}
