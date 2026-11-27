using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;
using Xunit;
namespace WowDi.Verification.Bounds.Tests;
public sealed class BoundsTests
{
    private static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"60"}],"currencies":["USD"],"calendars":["shift-a"]}""");
    private static readonly SolverIdentity Solver=new("fixture","1","managed");
    private static readonly SolverCapabilities Manifest=new(Solver,["integer.linear"],[ScalarKind.Integer,ScalarKind.Rational,ScalarKind.Money,ScalarKind.Boolean],null,null,1,false,true,true);
    private static readonly Evidence Attestation=new(EvidenceSource.SolverAttestation,"fixture-proof");
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    private static (UdirDocument Doc,EvidenceBinding Binding,byte[] Facts) Setup(Direction direction=Direction.Maximize,string available="8",bool impossible=false,bool soft=false)
    {
        var original=UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"))).Model;
        var facts=Encoding.UTF8.GetBytes("{\"available\":\""+available+"\"}");
        var symbols=impossible?original.Symbols.Select(s=>s.Id=="x"?s with{Lower="9"}:s).ToImmutableArray():original.Symbols;
        var d=UdirDocument.Create(original with{Symbols=symbols,Scenarios=[],Requires=["integer.linear"],InputDigests=[Hash(facts)],
            Objectives=[original.Objectives[0] with{Direction=direction,Weight="1",Tolerance="0"}],
            Constraints=soft?[original.Constraints[0] with{Hard=false,Penalty="2"}]:original.Constraints});
        return (d,new(new(d.Model.TenantId,d.Model.ModelId,d.Model.RevisionId,"run",Hash(d.Write())),Hash(facts),Hash(Catalog)),facts);
    }
    private static SolverResult Result(EvidenceBinding b,string? x,string? value,string? bound=null,Direction direction=Direction.Maximize,
        MathematicalStatus status=MathematicalStatus.Optimal,Termination termination=Termination.Timeout)=>
        new(b.Scope.TenantId,b.Scope.ModelId,b.Scope.RevisionId,b.Scope.RunId,Solver,status,termination,
            x is null?null:ImmutableDictionary<string,string>.Empty.Add("x",x),
            [new("revenue",value,bound is null?null:new(bound,direction,Attestation),"0","0")],Attestation,new(null,null,null));
    private static BoundAssessment Assess((UdirDocument Doc,EvidenceBinding Binding,byte[] Facts) s,SolverResult result,bool enumerate=false,int cap=4096)=>
        EvidenceClassifier.Assess(s.Doc,s.Binding,Catalog,s.Facts,new(s.Binding,result),Manifest,enumerate,cap);

    [Theory]
    [InlineData(Direction.Maximize,"8","56/3")]
    [InlineData(Direction.Minimize,"0","0")]
    public void ExhaustiveProofForBothDirections(Direction direction,string x,string optimum)
    {
        var s=Setup(direction);
        var a=Assess(s,Result(s.Binding,x,optimum,optimum,direction),true);
        Assert.False(a.Rejected,a.Diagnostic);
        Assert.Equal(MathematicalStatus.Optimal,a.Status);
        Assert.Equal(EvidenceStrength.ExactEnumeration,a.Strength);
        Assert.Equal(EvidenceStrength.ExactEnumeration,a.BoundStrength);
        Assert.Equal("0",a.AbsoluteGap);Assert.Equal("0",a.RelativeGap);
        Assert.Equal(13,a.Proof!.Checked);Assert.Equal(9,a.Proof.Feasible);
        Assert.Equal(x,a.Proof.Witness!["x"]);Assert.Equal(optimum,a.Proof.Optimum);
        Assert.Equal("USD",a.Currency);
        Assert.Equal(Termination.Timeout,a.Termination); // independent proof, not a timeout-derived claim
    }
    [Fact]
    public void SolverAttestationCannotBecomeProofEvenWithZeroGap()
    {
        var s=Setup();var r=Result(s.Binding,"3","7","7");
        var a=Assess(s,r);
        Assert.False(a.Rejected);
        Assert.Equal(MathematicalStatus.Feasible,a.Status);
        Assert.Equal(MathematicalStatus.Optimal,a.ReportedStatus);
        Assert.Equal(EvidenceStrength.IndependentlyVerifiedPrimal,a.Strength);
        Assert.Equal(EvidenceStrength.SolverAttestation,a.BoundStrength);
        Assert.Equal("0",a.AbsoluteGap);
        Assert.True(Assess(s,r,true).Rejected); // attested bound 7 contradicted by independent optimum
        var nonoptimal=Assess(s,Result(s.Binding,"3","7","20"),true);
        Assert.Equal("bounds.nonOptimalWitness",nonoptimal.Diagnostic);
        Assert.Equal(MathematicalStatus.Feasible,nonoptimal.Status);
        Assert.Equal("35/3",nonoptimal.AbsoluteGap);
        Assert.Equal("5/3",nonoptimal.RelativeGap);
    }
    [Fact]
    public void WrongDirectionImpossibleBoundStaleFactsAndForeignScopeReject()
    {
        var s=Setup();var result=Result(s.Binding,"3","7","8",Direction.Minimize);
        Assert.True(Assess(s,result).Rejected);
        Assert.True(Assess(s,Result(s.Binding,"3","7","6")).Rejected);
        Assert.True(Assess(s,Result(s.Binding,"9","21","25")).Rejected);
        var foreign=s.Binding with{Scope=s.Binding.Scope with{TenantId="foreign"}};
        Assert.True(EvidenceClassifier.Assess(s.Doc,s.Binding,Catalog,s.Facts,new(foreign,Result(s.Binding,"3","7")),Manifest).Rejected);
        Assert.True(EvidenceClassifier.Assess(s.Doc,s.Binding,Catalog,Encoding.UTF8.GetBytes("{\"available\":\"9\"}"),new(s.Binding,Result(s.Binding,"3","7")),Manifest).Rejected);
        Assert.True(EvidenceClassifier.Assess(s.Doc,s.Binding,Encoding.UTF8.GetBytes("{}"),s.Facts,new(s.Binding,Result(s.Binding,"3","7")),Manifest).Rejected);
        Assert.True(Assess(s,Result(s.Binding,"3","7") with{RunId="old"}).Rejected);
    }
    [Fact]
    public void TimeoutAndInfeasibleAttestationAreUnknownWithoutCompleteEnumeration()
    {
        var s=Setup(impossible:true);
        var r=Result(s.Binding,null,null,status:MathematicalStatus.Infeasible);
        Assert.Equal(MathematicalStatus.Unknown,Assess(s,r).Status);
        var a=Assess(s,r,true);
        Assert.Equal(MathematicalStatus.Infeasible,a.Status);
        Assert.Equal(EvidenceStrength.ExactEnumeration,a.Strength);
        Assert.Equal(4,a.Proof!.Checked);Assert.Equal(0,a.Proof.Feasible);
        var limited=Assess(s,r,true,2);
        Assert.Equal(MathematicalStatus.Unknown,limited.Status);
        Assert.Null(limited.Proof);
        Assert.Equal("bounds.enumerationUnavailable",limited.Diagnostic);
        Assert.True(Assess(Setup(),r,true).Rejected); // feasible model disproves infeasible claim
    }
    [Fact]
    public void UnsupportedSoftProfileAndForgedCertificateCannotPass()
    {
        var s=Setup(soft:true);
        Assert.True(Assess(s,Result(s.Binding,"3","7")).Rejected);
        s=Setup();
        var r=Result(s.Binding,"3","7","8");
        var row=r.Objectives[0];
        Assert.Equal("bounds.unverifiedCertificate",Assess(s,r with{Objectives=[row with{Bound=row.Bound! with{Evidence=new(EvidenceSource.IndependentCertificate,"forged")}}]}).Diagnostic);
        Assert.Equal("bounds.unverifiedCertificate",Assess(s,r with{StatusEvidence=new(EvidenceSource.IndependentCertificate,"forged")}).Diagnostic);
    }
    [Fact]
    public void TwoVariableNegativeObjectiveMatchesIndependentIntegerOracle()
    {
        var s=Setup(Direction.Minimize);
        var m=s.Doc.Model;
        var x=m.Symbols.Single(z=>z.Id=="x");
        var d=UdirDocument.Create(m with
        {
            Symbols=m.Symbols.Select(z=>z.Id=="x"?z with{Upper="4"}:z).Append(x with{Id="y",Upper="4"}).ToImmutableArray(),
            Expressions=m.Expressions.Select(e=>e.Id switch
            {
                "price"=>e with{Literal="-7/3"},
                "ok"=>e with{Arguments=["sum","capacity"]},
                "return"=>e with{Arguments=["sum","price"]},
                _=>e
            }).Concat([new Expression("qy",Operation.Symbol,"minutes",[],x.Span,SymbolId:"y"),
                new Expression("sum",Operation.Add,"minutes",["q","qy"],x.Span)]).ToImmutableArray()
        });
        var b=s.Binding with{Scope=s.Binding.Scope with{ModelSha256=Hash(d.Write())}};
        var r=Result(b,"3","-14","-20",Direction.Minimize) with
        {Incumbent=ImmutableDictionary<string,string>.Empty.Add("x","3").Add("y","3")};
        var a=Assess((d,b,s.Facts),r,true);
        var oracle=Enumerable.Range(0,5).SelectMany(i=>Enumerable.Range(0,5).Select(j=>new{Sum=i+j,Numerator=-7*(i+j)}))
            .Where(t=>t.Sum<=8).ToArray();
        Assert.False(a.Rejected,a.Diagnostic);
        Assert.Equal(oracle.Length,a.Proof!.Feasible);
        Assert.Equal(25,a.Proof.Checked);
        Assert.Equal($"{oracle.Min(t=>t.Numerator)}/3",a.Proof.Optimum);
        Assert.Equal("14/3",a.AbsoluteGap);
        Assert.Equal("1/3",a.RelativeGap);
        Assert.Equal(MathematicalStatus.Feasible,a.Status);
        Assert.Equal("4",a.Proof.Witness!["x"]);Assert.Equal("4",a.Proof.Witness["y"]);
    }

    [Fact]
    public void UnknownBoundAndIndependentWitnessAreNotFabricatedSolverIncumbents()
    {
        var s=Setup();var r=Result(s.Binding,null,null,status:MathematicalStatus.Unknown);
        var a=Assess(s,r);
        Assert.Null(a.Bound);Assert.Null(a.AbsoluteGap);Assert.Null(a.RelativeGap);
        Assert.Equal(MathematicalStatus.Unknown,a.Status);
        var proof=Assess(s,r,true);
        Assert.Equal(MathematicalStatus.Unknown,proof.Status);
        Assert.Null(proof.Incumbent);Assert.Equal("56/3",proof.Bound);Assert.Equal("8",proof.Proof!.Witness!["x"]);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>EvidenceClassifier.Assess(s.Doc,s.Binding,Catalog,s.Facts,new(s.Binding,r),Manifest,true,ct:cancel.Token));
    }
}
