using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Syntax;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class SyntaxTests
{
    private static DmlParseResult Parse(string text,string version=DmlParser.GrammarVersion) =>
        DmlParser.Parse("input",Encoding.UTF8.GetBytes(text),version);
    private static ProblemSyntax Valid(string text)=>Assert.IsType<ProblemSyntax>(Parse(text).Problem);
    private static string Slice(string text,SourceSpan span)=>text.Substring(span.Start,span.Length);

    [Theory]
    [InlineData("batch",10)]
    [InlineData("workforce",6)]
    public void PublishedExamplesHaveSourceLinkedAst(string name,int count)
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures",name+".dml"));
        var result=DmlParser.Parse(name,bytes);
        Assert.True(result.Accepted,result.Diagnostic?.Message);
        var problem=result.Problem!;var text=Encoding.UTF8.GetString(bytes);
        Assert.Equal(count,problem.Declarations.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),result.Source!.Sha256);
        Assert.Equal(text.Length,result.Source.Utf16Length);
        Assert.StartsWith("problem ",Slice(text,problem.Span));
        Assert.EndsWith("}",Slice(text,problem.Span));
        Assert.All(problem.Declarations,d=>Assert.Equal(name,d.Span.SourceId));
        var constraint=Assert.Single(problem.Declarations.OfType<ConstraintDeclaration>());
        Assert.IsType<BinarySyntax>(constraint.Left);
        Assert.Equal(name=="batch"?"<=":">=",Slice(text,constraint.Relation.Span));
        Assert.Null(constraint.Penalty);
        var objective=Assert.Single(problem.Declarations.OfType<ObjectiveDeclaration>());
        Assert.Equal("1",objective.Priority.Text);
        if(name=="batch")
        {
            Assert.Equal("orders.v1",Assert.Single(problem.Declarations.OfType<SourceDeclaration>()).Schema.Text);
            Assert.Equal("orders",Assert.Single(problem.Declarations.OfType<TriggerDeclaration>()).Source.Text);
            var scenario=Assert.Single(problem.Declarations.OfType<ScenarioDeclaration>());
            Assert.Equal("stock",Assert.Single(scenario.Overrides).Name.Text);
        }
    }

    [Fact]
    public void OperatorPrecedenceAndAssociativityAreExplicit()
    {
        var text="problem P { param x : Rational = -1.25 + 2 * (3 - 4) / 5; }";
        var value=Assert.IsType<BinarySyntax>(Assert.IsType<ValueDeclaration>(Assert.Single(Valid(text).Declarations)).Value);
        Assert.Equal(TokenKind.Plus,value.Operator.Kind);
        Assert.IsType<UnarySyntax>(value.Left);
        var divide=Assert.IsType<BinarySyntax>(value.Right);Assert.Equal(TokenKind.Slash,divide.Operator.Kind);
        var multiply=Assert.IsType<BinarySyntax>(divide.Left);Assert.Equal(TokenKind.Star,multiply.Operator.Kind);
        Assert.IsType<ParenthesizedSyntax>(multiply.Right);
        Assert.Equal("-1.25 + 2 * (3 - 4) / 5",Slice(text,value.Span));
        var sub=Assert.IsType<BinarySyntax>(Assert.IsType<ValueDeclaration>(Assert.Single(Valid("problem P { param x : Int = 8-3-2; }").Declarations)).Value);
        Assert.IsType<BinarySyntax>(sub.Left);
    }

    [Fact]
    public void ExplicitSoftAndBooleanFormsDoNotAlterHardDefault()
    {
        var p=Valid("problem P { decision enabled : Bool; constraint keep : enabled == true; constraint cost : 1 <= 2 soft penalty 1.25; }");
        var decision=Assert.IsType<DecisionDeclaration>(p.Declarations[0]);
        Assert.Null(decision.Lower);Assert.Null(decision.Upper);
        Assert.Null(Assert.IsType<ConstraintDeclaration>(p.Declarations[1]).Penalty);
        Assert.Equal("1.25",Assert.IsType<ConstraintDeclaration>(p.Declarations[2]).Penalty!.Text);
    }

    [Fact]
    public void WindowDependenciesAndPolicyAreDataOnly()
    {
        var p=Valid("problem P { window period from \"2026-01-01T00:00:00Z\" until \"2026-01-02T00:00:00Z\" calendar shift; depends b on a; policy safety hard row; }");
        Assert.Equal("shift",Assert.IsType<WindowDeclaration>(p.Declarations[0]).Calendar.Text);
        Assert.Equal("a",Assert.IsType<DependencyDeclaration>(p.Declarations[1]).DependsOn.Text);
        Assert.Equal("row",Assert.IsType<PolicyDeclaration>(p.Declarations[2]).Constraint.Text);
    }

    [Fact]
    public void Utf16SpansAndCrLfDiagnosticAreAccurate()
    {
        var text="problem P {\r\n source x schema \"عربي😀\";\r\n param y : Int = ;\r\n}";
        var result=Parse(text);
        Assert.False(result.Accepted);Assert.Null(result.Problem);
        var d=result.Diagnostic!;
        Assert.Equal("DML-SYN-006",d.Code);
        Assert.Equal(3,d.Line);Assert.Equal(18,d.Column);
        Assert.Equal(";",Slice(text,d.Span));
        Assert.Equal(text.Length,result.Source!.Utf16Length);
        Assert.DoesNotContain("عربي",d.Message);
    }

    [Theory]
    [InlineData("problem P { param x : Int = 1.; }","DML-LEX-002")]
    [InlineData("problem P { source s schema \"x\\q\"; }","DML-LEX-003")]
    [InlineData("problem P { source s schema \"x","DML-LEX-003")]
    [InlineData("problem P { source s schema \"\\uD800\"; }","DML-LEX-003")]
    [InlineData("problem P { param x : Int = 1e3; }","DML-SYN-001")]
    [InlineData("problem P { param x : Int = 1 }","DML-SYN-001")]
    [InlineData("problem P { constraint x : 1 + 2; }","DML-SYN-002")]
    [InlineData("problem P { minimize x priority 1.5 : 1; }","DML-SYN-003")]
    [InlineData("problem P { execute x; }","DML-SYN-004")]
    [InlineData("problem P { param x : String = 1; }","DML-SYN-005")]
    [InlineData("problem P { param x : Int = System.IO.File.ReadAllText(\"secret\"); }","DML-LEX-004")]
    [InlineData("problem P { param x : Int = exec(\"secret\"); }","DML-SYN-001")]
    [InlineData("problem P { param x : Int = $secret; }","DML-LEX-004")]
    [InlineData("problem P { scenario a { on change x reoptimize; } }","DML-SYN-001")]
    [InlineData("problem P { on change x reoptimize(); }","DML-SYN-001")]
    [InlineData("problem P {} problem Q {}","DML-SYN-001")]
    [InlineData("problem P { param x : Int[kg/] = 1; }","DML-SYN-001")]
    [InlineData("problem P { decision x : Int; }","DML-SYN-001")]
    [InlineData("problem P { param x : Int = ; }","DML-SYN-006")]
    [InlineData("problem P {","DML-SYN-004")]
    [InlineData("problem problem {}","DML-SYN-001")]
    public void InvalidSyntaxFailsClosedWithStableCode(string text,string code)
    {
        var result=Parse(text);
        Assert.False(result.Accepted);Assert.Null(result.Problem);
        Assert.Equal(code,result.Diagnostic!.Code);
        Assert.InRange(result.Diagnostic.Span.Start,0,text.Length);
        Assert.InRange(result.Diagnostic.Span.Length,0,text.Length-result.Diagnostic.Span.Start);
        Assert.Equal(result.Diagnostic,Parse(text).Diagnostic);
    }

    [Fact]
    public void NumericAndUnitTokensPreserveExactSpelling()
    {
        var p=Valid("problem P { param x : Rational[USD/hour/employee] = 0.000000000000000000000000001[USD/hour/employee]; }");
        var decl=Assert.IsType<ValueDeclaration>(Assert.Single(p.Declarations));
        var literal=Assert.IsType<NumberSyntax>(decl.Value);
        Assert.Equal("0.000000000000000000000000001",literal.Number.Text);
        Assert.Equal("USD/hour/employee",string.Concat(literal.Unit!.Parts.Select(t=>t.Text)));
    }

    [Fact]
    public void BoundedInputRejectsDepthWidthAndExcessiveTokenCount()
    {
        string Wrap(string x)=>"problem P { param x : Int = "+x+"; }";
        Assert.Equal("DML-LIMIT-001",DmlParser.Parse("input",new byte[DmlParser.MaximumBytes+1]).Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-002",Parse("problem P { "+string.Concat(Enumerable.Repeat("param x : Int = 1;",10000))+"}").Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-003",Parse("problem "+new string('a',129)+" {}").Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-004",Parse(Wrap(new string('9',257))).Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-005",Parse("problem P { source s schema \""+new string('x',4097)+"\"; }").Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-006",Parse(Wrap("1["+string.Join("*",Enumerable.Repeat("abcdefgh",40))+"]")).Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-007",Parse(Wrap(new string('(',65)+"1"+new string(')',65))).Diagnostic!.Code);
        Assert.Equal("DML-LIMIT-007",Parse(Wrap(string.Join("+",Enumerable.Repeat("1",65)))).Diagnostic!.Code);
        Assert.True(Parse(Wrap(string.Join("+",Enumerable.Repeat("1",64)))).Accepted);
        Assert.Equal("DML-VERSION-001",Parse("problem P {}","dml/0.2").Diagnostic!.Code);
        Assert.Equal("DML-LEX-001",DmlParser.Parse("input",new byte[]{0xff}).Diagnostic!.Code);
    }

    [Fact]
    public void CancelledParsingDoesNotProducePartialAst()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>DmlParser.Parse("input","problem P {}"u8.ToArray(),cancellationToken:cts.Token));
    }

    [Fact]
    public void SyntaxDoesNotClaimSemanticOrExternalAuthorization()
    {
        var p=Valid("problem P { source x schema \"file:///etc/passwd\"; on change x reoptimize; param x : Int = missing / 0; }");
        Assert.Equal(3,p.Declarations.Length);
        Assert.Equal("file:///etc/passwd",Assert.IsType<SourceDeclaration>(p.Declarations[0]).Schema.Text);
        // M06 resolves source aliases and references; M07 validates arithmetic, not syntax.
    }

    [Fact]
    public void DeterministicFuzzInputsNeverEscapeBoundedDiagnostics()
    {
        var rng=new Random(24018);
        const string alphabet="abcdefghijklmnopqrstuvwxyz0123456789{}[]();: =+-*/<>\"\\\r\n";
        for(int i=0;i<2000;i++)
        {
            var text=new string(Enumerable.Range(0,rng.Next(0,200)).Select(_=>alphabet[rng.Next(alphabet.Length)]).ToArray());
            if(i%2==0)text="problem P { param x : Int = "+text+"; }";
            var a=Parse(text);var b=Parse(text);
            Assert.Equal(a.Accepted,b.Accepted);Assert.Equal(a.Diagnostic,b.Diagnostic);
            Assert.True(a.Accepted||a.Diagnostic is not null);
        }
    }
}
