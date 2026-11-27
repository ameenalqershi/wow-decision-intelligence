using System.Security.Cryptography;
using System.Text;
using WowDi.Contracts;
using WowDi.Dml.Binding;
using WowDi.Dml.Lowering;
using WowDi.Dml.Semantics;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class PlanningTests
{
    private static DmlCompilation Compile(string body,string feature="integer.linear")
    {
        var b=DmlBinder.Bind("src",Encoding.UTF8.GetBytes("problem P { "+body+" }"),new BindingContext());
        Assert.True(b.Accepted,b.Diagnostic?.Message);
        var c=DmlCompiler.Compile(b.Document!,new("tenant","model","rev",new("shift","UTC","2026-01-01T00:00:00Z","minute"),
            new([new("minute","time","1")],[],["shift"]),[Convert.ToHexStringLower(SHA256.HashData("catalog"u8))],[],[feature]));
        Assert.True(c.Accepted,c.Diagnostic?.Message);return c.Compilation!;
    }
    // Test-only manifests exercise policy, not native qualification, execution or evidence.
    private static AdapterProfile Profile(string id,SolverFamily family,string rid="test-rid")=>new(
        new(new(id,"1",rid),family==SolverFamily.CpSat?["integer.linear"]:["integer.linear","rational.linear"],
            family==SolverFamily.CpSat?[ScalarKind.Integer,ScalarKind.Boolean]:[ScalarKind.Integer,ScalarKind.Boolean,ScalarKind.Rational],
            null,null,4,false,true,true),family,family==SolverFamily.CpSat?NumericEncoding.Int64:NumericEncoding.Binary64ExactInput,
        new string('a',64),1,1024,TimeSpan.FromMilliseconds(1));
    private static PlanningRequest Request()=>new("test-rid",new(8,4096,TimeSpan.FromSeconds(1)),NumericRequirement.ExactInput);
    private static PlanningResult Plan(DmlCompilation c,PlanningRequest request,params AdapterProfile[] profiles)=>
        SolverPlanner.Plan(c.Normalized,request,profiles,c.GeneratedInputs[c.FactsDigest].ToArray());

    [Fact]
    public void IntegralPreferenceAndOrdinalTieBreakAreStableUnderReordering()
    {
        var c=Compile("decision x : Int in [0,4]; constraint row : x <= 3; maximize goal priority 1 : x;");
        var a=Profile("a",SolverFamily.CpSat);var z=Profile("z",SolverFamily.CpSat);var h=Profile("h",SolverFamily.Highs);
        var first=Plan(c,Request(),z,h,a);var second=Plan(c,Request(),a,h,z);
        Assert.True(first.Accepted);Assert.Equal("a",first.Selection!.Profile.Manifest.Solver.SolverId);
        Assert.Equal(first.Selection.Profile,second.Selection!.Profile);
        Assert.Equal(first.Assessments.ToArray(),second.Assessments.ToArray());
        Assert.Equal(4,first.Selection.Budget.Threads);
        Assert.Equal(c.FactsDigest,first.Selection.Lowered.FactsDigest);
        Assert.Equal(1,first.Features.IntegralVariables);
        Assert.Equal(3,first.Assessments.Count(x=>x.Eligible));
    }
    [Fact]
    public void ContinuousOnlyAdmitsExactInputWithManifestSupport()
    {
        var c=Compile("decision x : Rational in [0,1/2]; minimize goal priority 1 : x;","rational.linear");
        var p=Plan(c,Request(),Profile("c",SolverFamily.CpSat),Profile("h",SolverFamily.Highs));
        Assert.Equal("h",p.Selection!.Profile.Manifest.Solver.SolverId);
        Assert.Equal("plan.capability",p.Assessments.Single(x=>x.Solver.SolverId=="c").Code);
        var strict=Plan(c,Request() with{NumericRequirement=NumericRequirement.IntegerArithmetic},Profile("h",SolverFamily.Highs));
        Assert.False(strict.Accepted);Assert.Equal("plan.numeric",Assert.Single(strict.Assessments).Code);
    }
    [Fact]
    public void IncompatibleRuntimeResourcesBoundsAndInterruptionRejectBeforeLowering()
    {
        var c=Compile("decision x : Int in [0,4];");
        var p=Profile("valid",SolverFamily.CpSat);
        var result=Plan(c,Request() with{RequireBounds=true},
            Profile("rid",SolverFamily.CpSat,"other"),
            Profile("mem",SolverFamily.CpSat) with{MinimumMemoryBytes=8192},
            Profile("deadline",SolverFamily.CpSat) with{MinimumDeadline=TimeSpan.FromSeconds(2)},
            Profile("interrupt",SolverFamily.CpSat) with{Manifest=p.Manifest with{Solver=new("interrupt","1","test-rid"),SupportsInterruption=false}},
            Profile("bounds",SolverFamily.CpSat) with{Manifest=p.Manifest with{Solver=new("bounds","1","test-rid"),SupportsBounds=false}});
        Assert.False(result.Accepted);
        Assert.Equal("plan.runtime",result.Assessments.Single(x=>x.Solver.SolverId=="rid").Code);
        Assert.Equal("plan.budget",result.Assessments.Single(x=>x.Solver.SolverId=="mem").Code);
        Assert.Equal("plan.budget",result.Assessments.Single(x=>x.Solver.SolverId=="deadline").Code);
        Assert.Equal("plan.interruption",result.Assessments.Single(x=>x.Solver.SolverId=="interrupt").Code);
        Assert.Equal("plan.bounds",result.Assessments.Single(x=>x.Solver.SolverId=="bounds").Code);
    }
    [Fact]
    public void ActualLoweringRejectsNonlinearAndNonDyadicModelsDespiteManifest()
    {
        var nonlinear=Compile("decision x : Int in [0,3]; minimize goal priority 1 : x*x;");
        var result=Plan(nonlinear,Request(),Profile("c",SolverFamily.CpSat),Profile("h",SolverFamily.Highs));
        Assert.False(result.Accepted);Assert.All(result.Assessments,a=>Assert.Equal("lower.nonlinear",a.Code));
        var nonDyadic=Compile("decision x : Rational in [0,1/3];","rational.linear");
        Assert.Equal("lower.rounding",Assert.Single(Plan(nonDyadic,Request(),Profile("h",SolverFamily.Highs)).Assessments).Code);
    }
    [Fact]
    public void InvalidCatalogOrRequestFailsClosedAndEmptyCatalogDoesNotInventBackend()
    {
        var c=Compile("decision x : Int in [0,1];");
        var p=Profile("c",SolverFamily.CpSat);
        Assert.False(Plan(c,Request()).Accepted);
        Assert.Equal("plan.catalog",Assert.Throws<ContractException>(()=>Plan(c,Request(),p,p)).Code);
        Assert.Equal("plan.catalog",Assert.Throws<ContractException>(()=>Plan(c,Request(),p with{QualificationSha256="unqualified"})).Code);
        Assert.Equal("plan.request",Assert.Throws<ContractException>(()=>Plan(c,Request() with{Budget=new(0,1,TimeSpan.FromSeconds(1))},p)).Code);
        using var token=new CancellationTokenSource();token.Cancel();
        Assert.Throws<OperationCanceledException>(()=>SolverPlanner.Plan(c.Normalized,Request(),[p],ct:token.Token));
    }
}
