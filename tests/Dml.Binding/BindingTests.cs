using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Binding;
using WowDi.Dml.Syntax;
using Xunit;

namespace WowDi.Tests;

public sealed class BindingTests
{
    private static BindingContext Context()=>new([new("orders","orders.v1")]);
    private static BindingResult Bind(string text,BindingContext? ctx=null)=>DmlBinder.Bind("input",Encoding.UTF8.GetBytes(text),ctx??Context());
    private static BoundDocument Valid(string text)=>Assert.IsType<BoundDocument>(Bind(text).Document);
    private static BindingDiagnostic Invalid(string text,string code,BindingContext? context=null)
    {
        var result=Bind(text,context);
        Assert.False(result.Accepted);Assert.Null(result.Document);
        var d=Assert.IsType<BindingDiagnostic>(result.Diagnostic);
        Assert.Equal(code,d.Code);
        Assert.InRange(d.Span.Start,0,text.Length);
        Assert.InRange(d.Span.Length,0,text.Length-d.Span.Start);
        return d;
    }

    [Theory]
    [InlineData("batch",9,10)]
    [InlineData("workforce",6,6)]
    public void RealExamplesRetainAllDeclarationsAndResolveUses(string name,int named,int total)
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures",name+".dml"));
        var result=DmlBinder.Bind(name,bytes,Context());
        Assert.True(result.Accepted,result.Diagnostic?.Message);
        var doc=result.Document!;
        Assert.Equal(named,doc.Declarations.Count);
        Assert.Equal(total,doc.Syntax.Declarations.Length);
        Assert.Equal(4,doc.ValueOrder.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)),doc.Source.Sha256);
        Assert.Equal(bytes,doc.SourceBytes.ToArray());
        var original=Encoding.UTF8.GetString(bytes);
        foreach(var reference in doc.References)
        {
            Assert.Equal(reference.Target.Name,original.Substring(reference.UseSpan.Start,reference.UseSpan.Length));
            Assert.Same(doc.Declarations[reference.Target.Name],reference.Target);
        }
        if(name=="batch")
        {
            Assert.Contains(doc.References,r=>r.Kind==ReferenceKind.Trigger&&r.Target.Name=="orders");
            Assert.Contains(doc.References,r=>r.Kind==ReferenceKind.Policy&&r.Target.Name=="available");
            Assert.Contains(doc.References,r=>r.Kind==ReferenceKind.Override&&r.Target.Name=="stock");
        }
    }

    [Fact]
    public void ForwardReferencesAndDuplicateUsesHaveDeterministicTopologicalOrder()
    {
        var doc=Valid("problem P { param z : Int = a+a; param a : Int = b; param b : Int = 2; decision q : Int in [0,z]; }");
        Assert.Equal(new[]{"b","a","z","q"},doc.ValueOrder.Select(x=>x.Name));
        Assert.Equal(2,doc.References.Count(r=>r.Target.Name=="a"));
    }

    [Fact]
    public void UnknownSymbolReportsNearestScopedSuggestionsAndSpan()
    {
        var text="problem P {\r\n resource stock : Int = 10;\r\n constraint row : stok <= 2;\r\n}";
        var d=Invalid(text,"DML-BIND-001");
        Assert.Equal("stok",text.Substring(d.Span.Start,d.Span.Length));
        Assert.Equal(3,d.Line);Assert.Equal(19,d.Column);
        Assert.Equal(new[]{"stock"},d.Suggestions.ToArray());
        Assert.Null(d.RelatedSpan);
        var repeated=Invalid(text,"DML-BIND-001");
        Assert.Equal(d.Code,repeated.Code);Assert.Equal(d.Span,repeated.Span);Assert.Equal(d.Suggestions.ToArray(),repeated.Suggestions.ToArray());
    }

    [Theory]
    [InlineData("problem P { param x : Int = 1; resource x : Int = 2; }","DML-BIND-002")]
    [InlineData("problem P { source x schema \"v\"; param y : Int = x; }","DML-BIND-006")]
    [InlineData("problem P { source orders schema \"orders.v1\"; param x : Int = orders; }","DML-BIND-003")]
    [InlineData("problem P { param a : Int = a; }","DML-BIND-004")]
    [InlineData("problem P { param a : Int = b; param b : Int = c; param c : Int = a; }","DML-BIND-004")]
    [InlineData("problem P { decision a : Int in [0,b]; param b : Int = a; }","DML-BIND-004")]
    [InlineData("problem P { decision a : Int in [0,1]; decision b : Int in [0,1]; depends a on b; depends b on a; }","DML-BIND-005")]
    [InlineData("problem P { resource a : Int = 1; depends a on a; }","DML-BIND-005")]
    [InlineData("problem P { source orders schema \"different\"; }","DML-BIND-006")]
    [InlineData("problem P { source orders schema \"orders.v1\"; on change orders reoptimize; on change orders reoptimize; }","DML-BIND-007")]
    [InlineData("problem P { param a : Int = 1; scenario s { set a = 2; set a = 3; } }","DML-BIND-007")]
    [InlineData("problem P { resource a : Int = 1; resource b : Int = 2; depends a on b; depends a on b; }","DML-BIND-007")]
    [InlineData("problem P { decision x : Int in [0,1]; scenario s { set x = 1; } }","DML-BIND-003")]
    [InlineData("problem P { constraint row : 1 <= 2 soft penalty 1; policy rule hard row; }","DML-BIND-008")]
    [InlineData("problem P { param x : Int = 1; policy rule hard x; }","DML-BIND-003")]
    [InlineData("problem P { on change orders reoptimize; }","DML-BIND-001")]
    [InlineData("problem P { param x : Int = System.IO.File.ReadAllText(\"x\"); }","DML-LEX-004")]
    [InlineData("problem P { param x : Int = exec(1); }","DML-SYN-001")]
    public void InvalidBindingsFailWithoutPartialDocument(string text,string code)=>Invalid(text,code);

    [Fact]
    public void CycleDiagnosticPointsToActualCycleAndNotDownstreamNode()
    {
        var text="problem P { param a : Int = b; param b : Int = c; param c : Int = b; }";
        var d=Invalid(text,"DML-BIND-004");
        Assert.NotNull(d.RelatedSpan);
        var target=text.Substring(d.RelatedSpan!.Start,d.RelatedSpan.Length);
        Assert.Contains(target,new[]{"b","c"});
    }

    [Fact]
    public void AuthorizedAliasesAreExplicitFrozenAndNeverFetched()
    {
        var sources=new[]{new AuthorizedSource("orders","orders.v1")};
        var ctx=new BindingContext(sources);
        sources[0]=new("orders","changed");
        Assert.True(Bind("problem P { on change orders reoptimize; source orders schema \"orders.v1\"; }",ctx).Accepted);
        Invalid("problem P { source orders schema \"file:///etc/passwd\"; }","DML-BIND-006",ctx);
        Invalid("problem P { source orders schema \"orders.v1\"; }","DML-BIND-006",new BindingContext());
        Assert.Throws<ArgumentException>(()=>new BindingContext([new("a","v"),new("a","v")]));
    }

    [Fact]
    public void DeepAcyclicChainDoesNotUseRecursiveDependencyTraversal()
    {
        var text=new StringBuilder("problem P { param p0 : Int = 1;");
        for(int i=1;i<4000;i++)text.Append("param p").Append(i).Append(" : Int = p").Append(i-1).Append(';');
        text.Append('}');
        var doc=Valid(text.ToString());
        Assert.Equal(4000,doc.ValueOrder.Length);
        Assert.Equal("p0",doc.ValueOrder[0].Name);
        Assert.Equal("p3999",doc.ValueOrder[^1].Name);
    }

    [Fact]
    public void SourceLimitsAndCancellationRetainSyntaxDiagnostics()
    {
        var result=DmlBinder.Bind("input",new byte[DmlParser.MaximumBytes+1],Context());
        Assert.Equal("DML-LIMIT-001",result.Diagnostic!.Code);
        Invalid("problem P { param a : Int = "+new string('(',65)+"1"+new string(')',65)+"; }","DML-LIMIT-007");
        using var token=new CancellationTokenSource();token.Cancel();
        Assert.Throws<OperationCanceledException>(()=>DmlBinder.Bind("input","problem P {}"u8.ToArray(),Context(),cancellationToken:token.Token));
    }

    [Fact]
    public void BoundSourceIsImmutableAndSemanticErrorsRemainSeparate()
    {
        var bytes=Encoding.UTF8.GetBytes("problem P { param a : Int[kg] = 1[USD]/0; }");
        var result=DmlBinder.Bind("input",bytes,Context());
        Assert.True(result.Accepted);
        bytes[0]=0;
        Assert.Equal((byte)'p',result.Document!.SourceBytes[0]);
        // No evaluation or dimensional admission in M06. M07 must reject unsupported or invalid semantics.
    }
}
