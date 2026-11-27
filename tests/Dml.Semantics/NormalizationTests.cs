using System.Collections.Immutable;
using System.Security.Cryptography;
using WowDi.Dml.Semantics;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class NormalizationTests
{
    private static readonly SourceSpan Span=new("source",0,1);
    private static readonly ScalarType Integer=new("i",ScalarKind.Integer);
    private static readonly ScalarType Bool=new("b",ScalarKind.Boolean);
    private static readonly ScalarType Rat=new("r",ScalarKind.Rational);
    private static MeasurementSemantics Catalog()=>new([new("minute","time","1"),new("kg","mass","1")],["USD"],["shift"]);
    private static UdirDocument Document(ImmutableArray<Expression> expressions,ImmutableArray<Constraint> rows,
        ImmutableArray<Objective> goals,ImmutableArray<Symbol> symbols,ImmutableArray<ScalarType>? types=null)
    {
        var digest=Convert.ToHexStringLower(SHA256.HashData("x"u8));
        return UdirDocument.Create(new("tenant","model","r1","udir/1",[digest],[new("source",digest,1)],
            new("shift","UTC","2026-01-01T00:00:00Z","minute"),types??[Integer,Bool,Rat],
            symbols,expressions,rows,goals,[],[]));
    }
    private static Expression Literal(string id,string value,string type="i")=>new(id,Operation.Literal,type,[],Span,Literal:value);
    private static Expression Ref(string id,string symbol,string type="i")=>new(id,Operation.Symbol,type,[],Span,SymbolId:symbol);
    private static Expression Binary(string id,Operation op,string left,string right,string type="i")=>new(id,op,type,[left,right],Span);
    private static NormalizedModel Normalize(UdirDocument d)
    {
        var result=ExactNormalizer.Normalize(d,Catalog());
        Assert.True(result.Accepted,result.Diagnostic?.Message);
        return result.Model!;
    }
    private static Objective Goal(string id)=>new("goal",id,Direction.Minimize,1,"1","0",Span);

    [Fact]
    public void EquivalentCommutativeAndConstantExpressionsHaveSameMathematicalProjection()
    {
        var variables=ImmutableArray.Create(new Symbol("x",SymbolRole.Variable,"i",Span,"0","5"));
        var first=Document([Ref("a","x"),Literal("b","2"),Literal("c","3"),Binary("d",Operation.Add,"b","c"),Binary("e",Operation.Add,"a","d")],[],[Goal("e")],variables);
        var second=Document([Literal("five","5"),Ref("ref","x"),Binary("sum",Operation.Add,"five","ref")],[],[Goal("sum")],variables);
        var a=Normalize(first);var b=Normalize(second);
        Assert.Equal(a.SemanticDigest,b.SemanticDigest);
        Assert.Equal(a.CanonicalBytes.ToArray(),b.CanonicalBytes.ToArray());
        Assert.Equal(5,a.Origins.Length);
        Assert.Equal("constant-fold",a.Origins.Single(x=>x.OriginalId=="d").Rewrite);
        Assert.Same(first,a.Original);
        Assert.Equal(first.Write(),a.Original.Write());
    }

    [Fact]
    public void IndependentFiniteEnumerationPreservesEveryAssignmentAndObjective()
    {
        // Test oracle evaluates original integer arithmetic separately from the compiler.
        var vars=ImmutableArray.Create(new Symbol("x",SymbolRole.Variable,"i",Span,"0","4"),
            new Symbol("y",SymbolRole.Variable,"i",Span,"0","4"));
        var d=Document([Ref("xRef","x"),Ref("yRef","y"),Literal("a","2"),Literal("b","3"),
            Binary("five",Operation.Add,"a","b"),Binary("sum",Operation.Add,"xRef","yRef"),
            Binary("predicate",Operation.LessOrEqual,"sum","five","b"),
            Binary("cost",Operation.Multiply,"sum","five")],
            [new("limit","predicate",true,Span),new("soft","predicate",false,Span,"2")],[Goal("cost")],vars);
        var normalized=Normalize(d);
        Assert.Equal(2,normalized.Constraints.Length); // no soft or hard row removed
        var rows=normalized.Constraints.ToDictionary(c=>c.Id);
        var nodes=normalized.Expressions;
        int Evaluate(string id,int x,int y)
        {
            var e=nodes[id];
            return e.Operation switch
            {
                Operation.Literal=>int.Parse(e.Literal!,System.Globalization.CultureInfo.InvariantCulture),
                Operation.Symbol=>e.SymbolId=="x"?x:y,
                Operation.Add=>Evaluate(e.Arguments[0],x,y)+Evaluate(e.Arguments[1],x,y),
                Operation.Multiply=>Evaluate(e.Arguments[0],x,y)*Evaluate(e.Arguments[1],x,y),
                Operation.LessOrEqual=>Evaluate(e.Arguments[0],x,y)<=Evaluate(e.Arguments[1],x,y)?1:0,
                _=>throw new InvalidOperationException()
            };
        }
        for(int x=0;x<=4;x++)for(int y=0;y<=4;y++)
        {
            Assert.Equal(x+y<=5,Evaluate(rows["limit"].PredicateId,x,y)==1);
            Assert.Equal(x+y<=5,Evaluate(rows["soft"].PredicateId,x,y)==1);
            Assert.Equal((x+y)*5,Evaluate(normalized.Objectives[0].ExpressionId,x,y));
        }
        Assert.Equal(d.Model.Expressions.Length,normalized.Origins.Length);
        Assert.Equal("2",rows["soft"].Penalty);
    }

    [Theory]
    [InlineData("-1","1")]
    [InlineData("0","3")]
    public void DivisorDomainIncludingZeroFailsClosed(string lower,string upper)
    {
        var doc=Document([Literal("one","1","r"),Ref("den","d","r"),Binary("div",Operation.Divide,"one","den","r")],
            [],[Goal("div")],[new("d",SymbolRole.Variable,"r",Span,lower,upper)]);
        var result=ExactNormalizer.Normalize(doc,Catalog());
        Assert.Equal("DML-DIV-001",result.Diagnostic!.Code);
        Assert.Equal(Span,result.Diagnostic.Span);
        Assert.Null(result.Model);
    }

    [Fact]
    public void ExactFractionAndOverflowNeverRound()
    {
        var exact=Normalize(Document([Literal("one","1","r"),Literal("three","3","r"),Binary("quot",Operation.Divide,"one","three","r")],[],[Goal("quot")],[]));
        Assert.Equal("1/3",exact.Expressions[exact.Objectives[0].ExpressionId].Literal);
        var fractional=Document([Literal("one","1"),Literal("three","3"),Binary("quot",Operation.Divide,"one","three")],[],[Goal("quot")],[]);
        Assert.Equal("DML-NUM-002",ExactNormalizer.Normalize(fractional,Catalog()).Diagnostic!.Code);
        var huge=new string('9',256);
        var overflow=Document([Literal("a",huge),Literal("b","2"),Binary("product",Operation.Multiply,"a","b")],[],[Goal("product")],[]);
        Assert.Equal("DML-NUM-001",ExactNormalizer.Normalize(overflow,Catalog()).Diagnostic!.Code);
    }

    [Fact]
    public void NonzeroDomainAndBooleanConstantsAreAdmitted()
    {
        var doc=Document([Literal("one","1","r"),Ref("den","d","r"),Binary("div",Operation.Divide,"one","den","r"),
            Literal("true","true","b"),Literal("false","false","b"),Binary("equal",Operation.Equal,"true","false","b")],
            [new("row","equal",true,Span)],[Goal("div")],[new("d",SymbolRole.Variable,"r",Span,"2","5")]);
        var result=Normalize(doc);
        Assert.Equal("false",result.Expressions[result.Constraints[0].PredicateId].Literal);
        Assert.Equal(Operation.Divide,result.Expressions[result.Objectives[0].ExpressionId].Operation);
    }

    [Fact]
    public void OriginalDimensionsAreValidatedBeforeAnyRewrite()
    {
        var kg=new ScalarType("kg",ScalarKind.Integer,"kg");
        var doc=Document([Literal("a","1","kg"),Literal("b","2","kg"),Binary("product",Operation.Multiply,"a","b")],[],[Goal("product")],[],[Integer,Bool,kg]);
        Assert.Equal("measure.expression",ExactNormalizer.Normalize(doc,Catalog()).Diagnostic!.Code);
    }

    [Fact]
    public void SemanticProjectionPreservesDirectionAndDomainAndIgnoresSourceIdsOnly()
    {
        var expr=ImmutableArray.Create(Ref("xRef","x"));
        var a=Normalize(Document(expr,[],[Goal("xRef")],[new("x",SymbolRole.Variable,"i",Span,"0","2")]));
        var b=Normalize(Document(expr,[],[Goal("xRef") with{Direction=Direction.Maximize}],[new("x",SymbolRole.Variable,"i",Span,"0","2")]));
        var c=Normalize(Document(expr,[],[Goal("xRef")],[new("x",SymbolRole.Variable,"i",Span,"0","3")]));
        Assert.NotEqual(a.SemanticDigest,b.SemanticDigest);
        Assert.NotEqual(a.SemanticDigest,c.SemanticDigest);
    }
}
