using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.Udir;
using WowDi.Verification.Independent;
using Xunit;

namespace WowDi.Optimization.Separability.Tests;
public sealed class ExactComponentsTests
{
    private static readonly MeasurementSemantics Catalog=new([new("minute","time","1")],[],["shift"]);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static UdirDocument Compile(string body)
    {
        var b=DmlBinder.Bind("source",Encoding.UTF8.GetBytes("problem P { "+body+" }"),new BindingContext());
        Assert.True(b.Accepted,b.Diagnostic?.Message);
        var c=DmlCompiler.Compile(b.Document!,new("tenant","model","r1",new("shift","UTC","2026-01-01T00:00:00Z","minute"),Catalog,[Hash("facts"u8.ToArray())],[],["integer.linear"]));
        Assert.True(c.Accepted,c.Diagnostic?.Message);return c.Compilation!.Original;
    }
    private static VerificationScope Scope(UdirDocument d)=>new(d.Model.TenantId,d.Model.ModelId,d.Model.RevisionId,"run",Hash(d.Write()));
    private static SeparationResult Solve(UdirDocument d,int budget=4096)=>ExactComponents.Solve(d,Catalog,Scope(d),budget);
    private const string Vars="decision x : Int in [0,2]; decision y : Int in [0,2]; decision z : Int in [0,2]; decision w : Int in [0,2]; ";
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(true,true)]
    public void FullIndependentOracleAndSharedCapacity(bool maximize,bool coupled)
    {
        var d=Compile(Vars+"constraint a : x+y >= 2; constraint b : z+w >= 1; "+
            (coupled?"constraint shared : x+z <= 1; ":"")+
            (maximize?"maximize":"minimize")+" goal priority 1 : 2*x+y+3*z+w+7;");
        var result=Solve(d);
        var feasible=(from x in Enumerable.Range(0,3) from y in Enumerable.Range(0,3)
                      from z in Enumerable.Range(0,3) from w in Enumerable.Range(0,3)
                      where x+y>=2&&z+w>=1&&(!coupled||x+z<=1)
                      select 2*x+y+3*z+w+7).ToArray();
        Assert.Equal(ComponentOutcome.VerifiedOptimum,result.Outcome);
        Assert.Equal((maximize?feasible.Max():feasible.Min()).ToString(CultureInfo.InvariantCulture),result.Objective);
        Assert.Equal(coupled?1:2,result.Components.Length);
        Assert.Equal(coupled?81:18,result.CheckedAssignments);
        Assert.True(result.Verification!.Accepted);
        Assert.Equal(Hash(d.Write()),result.ModelSha256);
    }
    [Fact]
    public void GlobalResourceAndCancelledTermsAreNotDropped()
    {
        var d=Compile(Vars+"constraint local : x+y >= 1; constraint shared : z+x-x <= 2; minimize goal priority 1 : x+z;");
        var r=Solve(d);
        Assert.Equal(ComponentOutcome.VerifiedOptimum,r.Outcome);
        Assert.Equal(2,r.Components.Length);
        Assert.Contains(r.Components,c=>c.Variables.SequenceEqual(new[]{"x","y","z"}));
        Assert.Equal(30,r.CheckedAssignments);
    }
    [Fact]
    public void InfeasibleConstantAndEmptyComponentAreDistinguishedFromBudget()
    {
        var a=Solve(Compile(Vars+"constraint no : 1 == 0; minimize goal priority 1 : x;"));
        Assert.Equal(ComponentOutcome.Infeasible,a.Outcome);Assert.Equal(0,a.CheckedAssignments);
        Assert.Contains("no",a.ConstantConstraints);
        var b=Solve(Compile(Vars+"constraint no : x+y >= 5; minimize goal priority 1 : x;"));
        Assert.Equal(ComponentOutcome.Infeasible,b.Outcome);Assert.Null(b.Assignment);
        var c=Solve(Compile(Vars+"constraint shared : x+y+z+w <= 2; minimize goal priority 1 : x;"),80);
        Assert.Equal(ComponentOutcome.BudgetExceeded,c.Outcome);Assert.Equal(0,c.CheckedAssignments);Assert.Null(c.Assignment);
    }
    [Theory]
    [InlineData("decision x : Int in [0,2]; decision y : Int in [0,2]; minimize goal priority 1 : x*y;")]
    [InlineData("decision x : Rational in [0,2]; minimize goal priority 1 : x;")]
    [InlineData("decision x : Int in [0,2]; constraint row : x <= 1 soft penalty 2; minimize goal priority 1 : x;")]
    [InlineData("decision x : Int in [0,2]; minimize a priority 1 : x; maximize b priority 2 : x;")]
    public void UnsupportedProfilesCannotProduceProof(string body)
    {
        var r=Solve(Compile(body));Assert.Equal(ComponentOutcome.Unsupported,r.Outcome);Assert.Null(r.Assignment);Assert.Null(r.Objective);
    }
    [Fact]
    public void BooleanRowsAndFixedFactAreAdmitted()
    {
        var d=Compile("decision flag : Bool; decision x : Int in [-2,2]; param p : Int = 2; constraint active : flag == true; minimize goal priority 1 : p*x;");
        var r=Solve(d);
        Assert.Equal(ComponentOutcome.VerifiedOptimum,r.Outcome);
        Assert.Equal("-4",r.Objective);Assert.Equal("true",r.Assignment!["flag"]);Assert.Equal("-2",r.Assignment["x"]);
        Assert.True(r.Verification!.Accepted);
    }
    [Fact]
    public void UnknownCapabilitiesAndForeignScopeFailClosedAndCancellationPropagates()
    {
        var d=Compile(Vars+"minimize goal priority 1 : x;");
        var unknown=UdirDocument.Create(d.Model with{Requires=["future.global"]});
        Assert.Equal(ComponentOutcome.Unsupported,Solve(unknown).Outcome);
        Assert.Equal(ComponentOutcome.Unsupported,ExactComponents.Solve(d,Catalog,Scope(d) with{TenantId="foreign"}).Outcome);
        using var c=new CancellationTokenSource();c.Cancel();
        Assert.Throws<OperationCanceledException>(()=>ExactComponents.Solve(d,Catalog,Scope(d),ct:c.Token));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void GeneratedPairsAgreeWithDirectIntegerOracle(int seed)
    {
        var random=new Random(seed);
        for(int trial=0;trial<16;trial++)
        {
            int a=random.Next(-3,4),b=random.Next(-3,4),c=random.Next(-3,4),e=random.Next(-3,4);
            int r=random.Next(-1,6),s=random.Next(-1,6);bool max=trial%2==0,coupled=trial%3==0;
            var d=Compile(Vars+FormattableString.Invariant($"constraint aa : x+y <= {r}; constraint bb : z+w >= {s}; ")+
                (coupled?"constraint global : x+w <= 2; ":"")+
                FormattableString.Invariant($"{(max?"maximize":"minimize")} goal priority 1 : {a}*x+{b}*y+{c}*z+{e}*w-5;"));
            var expected=(from x in Enumerable.Range(0,3) from y in Enumerable.Range(0,3)
                          from z in Enumerable.Range(0,3) from w in Enumerable.Range(0,3)
                          where x+y<=r&&z+w>=s&&(!coupled||x+w<=2)
                          select a*x+b*y+c*z+e*w-5).ToArray();
            var result=Solve(d);
            if(expected.Length==0){Assert.Equal(ComponentOutcome.Infeasible,result.Outcome);continue;}
            Assert.Equal(ComponentOutcome.VerifiedOptimum,result.Outcome);
            Assert.Equal((max?expected.Max():expected.Min()).ToString(CultureInfo.InvariantCulture),result.Objective);
            Assert.True(result.Verification!.Accepted);
        }
    }
}
