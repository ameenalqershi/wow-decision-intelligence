using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.Dml.Syntax;
using WowDi.Udir;
using WowDi.Solver.Contracts;
using WowDi.Verification.Independent;
using Xunit;

namespace WowDi.Tests;

public sealed class CompilerTests
{
    private static string Digest(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static DmlCompileOptions Options()=>new("tenant","model","r1",
        new("shift","UTC","2026-01-01T00:00:00Z","minute"),
        new([new("minute","time","1"),new("hour","time","60"),new("kg","mass","1"),new("g","mass","1/1000"),
            new("batch","batch","1"),new("employee","people","1")],["USD","EUR"],["shift"]),
        [Digest("frozen catalog fixture"u8)], [new("USD",ScalarKind.Money,"USD"),new("EUR",ScalarKind.Money,"EUR"),new("hour",ScalarKind.Duration),
            new("hour/employee",ScalarKind.Rate),new("USD/hour",ScalarKind.Rate)],["integer.linear"]);
    private static DmlCompilationResult Compile(string text,DmlCompileOptions? options=null)
    {
        var b=DmlBinder.Bind("dml",Encoding.UTF8.GetBytes(text),new([new("orders","orders.v1")]));
        Assert.True(b.Accepted,b.Diagnostic?.Message);
        return DmlCompiler.Compile(b.Document!,options??Options());
    }
    private static DmlCompilation Valid(string text)
    {
        var r=Compile(text);
        Assert.True(r.Accepted,r.Diagnostic?.Code+": "+r.Diagnostic?.Message);
        return r.Compilation!;
    }

    [Fact]
    public void ProductionCompilesRealSourceAndPreservesBranchFacts()
    {
        const string text="""
            problem Production {
              resource stock : Int[kg] = 10[kg];
              param consumption : Int[kg/batch] = 2[kg/batch];
              param margin : Int[USD/batch] = 4[USD/batch];
              decision qty : Int[batch] in [0[batch],8[batch]];
              constraint available : qty * consumption <= stock;
              maximize profit priority 1 : qty * margin;
              scenario shortage { set stock = 4[kg]; }
              scenario normal { set stock = 10[kg]; }
            }
            """;
        var compiled=Valid(text);
        var model=compiled.Original.Model;
        var stock=model.Symbols.Single(s=>s.Id=="stock");
        Assert.Equal("4",stock.Lower);Assert.Equal("10",stock.Upper);
        var root=model.Scenarios.Single(s=>s.ParentId is null);
        Assert.Equal("10",Assert.Single(root.Overrides).Value);
        Assert.Equal("4",model.Scenarios.Single(s=>s.Id=="shortage").Overrides[0].Value);
        Assert.Contains("staged.uncertainty",model.Requires);
        Assert.Equal(Digest(Encoding.UTF8.GetBytes(text)),Assert.Single(model.Sources).Sha256);
        foreach(var (digest,bytes) in compiled.GeneratedInputs)Assert.Equal(digest,Digest(bytes.AsSpan()));
        Assert.Contains(compiled.FactsDigest,model.InputDigests);
        var facts=JsonSerializer.Deserialize<Dictionary<string,string>>(compiled.GeneratedInputs[compiled.FactsDigest].AsSpan())!;
        Assert.Equal("10",facts["stock"]);Assert.Equal("2",facts["consumption"]);Assert.Equal("4",facts["margin"]);
        Assert.Equal(ScalarKind.Money,model.Types.Single(t=>t.Id==model.Expressions.Single(e=>e.Id==model.Objectives[0].ExpressionId).TypeId).Kind);
        Assert.Equal(model.Expressions.Length,compiled.Normalized.Origins.Length);
        Assert.All(model.Expressions,e=>Assert.InRange(e.Span.Start,0,text.Length-e.Span.Length));
        Assert.Equal(compiled.Normalized.CanonicalBytes.ToArray(),Valid(text).Normalized.CanonicalBytes.ToArray());
        // Independent integer oracle over all assignments and both branches.
        foreach(int capacity in new[]{4,10})for(int qty=0;qty<=8;qty++)
        {
            var env=new Dictionary<string,int>{{"qty",qty},{"stock",capacity},{"margin",4},{"consumption",2}};
            Assert.Equal(qty*2<=capacity,Eval(compiled.Normalized,compiled.Normalized.Constraints[0].PredicateId,env)==1);
            Assert.Equal(qty*4,Eval(compiled.Normalized,compiled.Normalized.Objectives[0].ExpressionId,env));
        }
    }

    [Fact]
    public void WorkforceCompilesRealSourceAndExhaustivelyPreservesCoverage()
    {
        const string text="""
            problem Workforce {
              decision staff : Int[employee] in [0[employee],12[employee]];
              param hoursPerStaff : Rational[hour/employee] = 8[hour/employee];
              param wage : Rational[USD/hour] = 15[USD/hour];
              param demand : Rational[hour] = 64[hour];
              constraint coverage : staff * hoursPerStaff >= demand;
              minimize cost priority 1 : staff * hoursPerStaff * wage;
            }
            """;
        var compiled=Valid(text);
        var m=compiled.Normalized;
        for(int staff=0;staff<=12;staff++)
        {
            var env=new Dictionary<string,int>{{"staff",staff},{"hoursPerStaff",8},{"wage",15},{"demand",64}};
            Assert.Equal(staff*8>=64,Eval(m,m.Constraints[0].PredicateId,env)==1);
            Assert.Equal(staff*8*15,Eval(m,m.Objectives[0].ExpressionId,env));
            // Enumerated test witness, never a solver result or optimality certificate.
            var candidate=new SolverResult("tenant","model","r1","enumeration",new("test-enumeration","1","test"),
                MathematicalStatus.Feasible,Termination.Completed,
                ImmutableDictionary<string,string>.Empty.Add("staff",staff.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                [new("cost",(staff*8*15).ToString(System.Globalization.CultureInfo.InvariantCulture),null,"0","0")],null,new(null,null,null));
            var scope=new VerificationScope("tenant","model","r1","enumeration",Digest(compiled.Original.Write()));
            var report=OriginalVerifier.Check(compiled.Original,Options().Measurements,scope,candidate,
                compiled.GeneratedInputs[compiled.FactsDigest].ToArray());
            Assert.Equal(staff*8>=64,report.Accepted);
            Assert.Equal(staff*8*15, int.Parse(Assert.Single(report.Objectives).ExactValue,System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal(Direction.Minimize,m.Objectives[0].Direction);
        Assert.Equal(ScalarKind.Money,m.Expressions[m.Objectives[0].ExpressionId].Type.Kind);
    }

    [Fact]
    public void ConstantDefinitionsExactFractionsAndSoftPenaltyAreRetained()
    {
        var c=Valid("problem P { param a : Rational = 1/3; param b : Rational = a + 0.5; decision x : Rational in [0,b]; constraint row : x <= b soft penalty 1.25; minimize goal priority 1 : x; }");
        var m=c.Original.Model;
        Assert.Equal("1/3",m.Symbols.Single(s=>s.Id=="a").Lower);
        Assert.Equal("5/6",m.Symbols.Single(s=>s.Id=="b").Lower);
        Assert.Equal("5/6",m.Symbols.Single(s=>s.Id=="x").Upper);
        Assert.False(m.Constraints[0].Hard);Assert.Equal("5/4",m.Constraints[0].Penalty);
        Assert.Equal(m.Expressions.Length,c.Normalized.Origins.Length);
    }

    [Fact]
    public void BooleanFactsAreFrozenNotInferredFromAnUnboundedDomain()
    {
        var c=Valid("problem P { param enabled : Bool = true; decision x : Bool; constraint row : x == enabled; }");
        var facts=JsonSerializer.Deserialize<Dictionary<string,string>>(c.GeneratedInputs[c.FactsDigest].AsSpan())!;
        Assert.Equal("true",facts["enabled"]);
        Assert.Null(c.Original.Model.Symbols.Single(s=>s.Id=="enabled").Lower);
        Assert.Null(c.Original.Model.Symbols.Single(s=>s.Id=="x").Lower);
    }

    [Theory]
    [InlineData("param a : Int[kg] = 1[USD];","DML-UNIT-001")]
    [InlineData("param a : Int[kg] = 1[g];","DML-UNIT-001")]
    [InlineData("param a : Int[unknown] = 1[unknown];","DML-UNIT-001")]
    [InlineData("param a : Int = 1/3;","DML-NUM-002")]
    [InlineData("param a : Rational = 1/0;","DML-DIV-001")]
    [InlineData("decision x : Int in [5,1];","DML-DOMAIN-001")]
    [InlineData("decision x : Bool in [false,true];","DML-DOMAIN-001")]
    [InlineData("decision x : Int in [0,1]; param a : Int = x;","DML-SEM-002")]
    [InlineData("param a : Int = 1; param b : Int = a; scenario s { set a = 2; }","DML-SEM-002")]
    [InlineData("param a : Int = 1; scenario s { set a = a+1; }","DML-SEM-002")]
    [InlineData("constraint row : 1 <= 2 soft penalty 0;","DML-DOMAIN-001")]
    [InlineData("minimize a priority 1 : 1; maximize b priority 1 : 2;","DML-OBJ-002")]
    [InlineData("minimize a priority 0 : 1;","DML-OBJ-002")]
    [InlineData("minimize a priority 1 : true;","DML-TYPE-001")]
    [InlineData("constraint row : true <= false;","DML-TYPE-001")]
    [InlineData("decision x : Rational in [-1,1]; minimize a priority 1 : 1/x;","DML-DIV-001")]
    [InlineData("window w from \"2026-01-01T00:00:00\" until \"2026-01-02T00:00:00Z\" calendar shift;","DML-TIME-001")]
    [InlineData("window w from \"2026-01-01T00:00:00Z\" until \"2026-01-02T00:00:00Z\" calendar shift;","DML-CAP-001")]
    [InlineData("source orders schema \"orders.v1\"; on change orders reoptimize;","DML-CAP-001")]
    [InlineData("decision x : Int in [0,1]; decision y : Int in [0,1]; depends x on y;","DML-CAP-001")]
    public void UnsupportedOrInvalidSourceFailsWithoutPublication(string body,string code)
    {
        var text="problem P { "+body+" }";
        var result=Compile(text);
        Assert.False(result.Accepted);Assert.Null(result.Compilation);
        Assert.Equal(code,result.Diagnostic!.Code);
        Assert.NotNull(result.Diagnostic.Span);
        Assert.InRange(result.Diagnostic.Span!.Start,0,text.Length-result.Diagnostic.Span.Length);
    }

    [Fact]
    public void HardPolicyAssertionAndExplicitSourceBytesAreRetained()
    {
        var compiled=Valid("problem P { decision x : Int in [0,1]; constraint safe : x <= 1; policy safety hard safe; }");
        Assert.True(Assert.Single(compiled.Original.Model.Constraints).Hard);
        var policy=Assert.Single(compiled.Bound.Syntax.Declarations.OfType<PolicyDeclaration>());
        Assert.Equal("safe",policy.Constraint.Text);
        Assert.Equal(compiled.Bound.SourceBytes.ToArray(),compiled.GeneratedInputs[compiled.Bound.Source.Sha256].ToArray());
    }

    [Fact]
    public void ExactUnitProductsRejectCurrencyMixAndUnrepresentableReciprocals()
    {
        var mix=Compile("problem P { param a : Rational[USD] = 2[USD]; param b : Rational[EUR] = 1[EUR]; minimize cost priority 1 : a / b; }");
        Assert.Equal("DML-FX-001",mix.Diagnostic!.Code);
        var reciprocal=Compile("problem P { param a : Int[kg] = 2[kg]; minimize cost priority 1 : 1 / a; }");
        Assert.Equal("DML-CAP-001",reciprocal.Diagnostic!.Code);
    }

    [Fact]
    public void UnitMismatchReportsBothSourceOperandsAndFxIsExplicit()
    {
        var text="problem P { param a : Rational[USD] = 2[USD]; param b : Rational[EUR] = 1[EUR]; minimize cost priority 1 : a + b; }";
        var r=Compile(text);
        Assert.Equal("DML-FX-001",r.Diagnostic!.Code);
        var span=r.Diagnostic.Span!;var related=r.Diagnostic.RelatedSpan!;
        Assert.Equal("b",text.Substring(span.Start,span.Length));
        Assert.Equal("a",text.Substring(related.Start,related.Length));
    }

    [Fact]
    public void LiteralOnlyArithmeticUsesExactCommonRepresentation()
    {
        var c=Valid("problem P { minimize cost priority 1 : 1 + 0.5; constraint row : 1/3 <= 0.5; }");
        Assert.Equal("3/2",c.Normalized.Expressions[c.Normalized.Objectives[0].ExpressionId].Literal);
        Assert.Equal("true",c.Normalized.Expressions[c.Normalized.Constraints[0].PredicateId].Literal);
    }

    [Fact]
    public void HugeArithmeticAndCancellationAreBounded()
    {
        var text="problem P { param a : Int = "+new string('9',256)+" * 2; }";
        Assert.Equal("DML-NUM-001",Compile(text).Diagnostic!.Code);
        var b=DmlBinder.Bind("dml","problem P {}"u8.ToArray(),new BindingContext()).Document!;
        using var token=new CancellationTokenSource();token.Cancel();
        Assert.Throws<OperationCanceledException>(()=>DmlCompiler.Compile(b,Options(),token.Token));
    }

    private static int Eval(NormalizedModel model,string id,Dictionary<string,int> values)
    {
        var e=model.Expressions[id];
        int A()=>Eval(model,e.Arguments[0],values);
        int B()=>Eval(model,e.Arguments[1],values);
        return e.Operation switch
        {
            Operation.Symbol=>values[e.SymbolId!],
            Operation.Literal=>int.Parse(e.Literal!,System.Globalization.CultureInfo.InvariantCulture),
            Operation.Multiply=>A()*B(),
            Operation.LessOrEqual=>A()<=B()?1:0,
            Operation.GreaterOrEqual=>A()>=B()?1:0,
            _=>throw new InvalidOperationException()
        };
    }
}
