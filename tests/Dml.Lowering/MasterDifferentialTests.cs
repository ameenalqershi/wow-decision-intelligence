using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Binding;
using WowDi.Dml.Lowering;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

// Decimal is an independent exact oracle on this intentionally small dyadic
// profile: every operand is bounded and all denominators divide powers of 2.
public sealed class MasterDifferentialTests
{
    private static readonly SolverCapabilities Manifest = new(new("managed-fixture","1","test"),
        ["integer.linear"],[ScalarKind.Integer,ScalarKind.Rational],null,null,1,false,true,false);
    private static decimal Number(string text)
    {
        var parts=text.Split('/');
        return decimal.Parse(parts[0],CultureInfo.InvariantCulture) /
            (parts.Length==1?1:decimal.Parse(parts[1],CultureInfo.InvariantCulture));
    }
    private static DmlCompilation Compile(string source)
    {
        var bound=DmlBinder.Bind("audit",Encoding.UTF8.GetBytes(source),new BindingContext());
        Assert.True(bound.Accepted,bound.Diagnostic?.Message);
        var options=new DmlCompileOptions("tenant","model","r1",new("shift","UTC","2026-01-01T00:00:00Z","minute"),
            new([new("minute","time","1")],[],["shift"]),[Convert.ToHexStringLower(SHA256.HashData("catalog"u8))],[],["integer.linear"]);
        var compiled=DmlCompiler.Compile(bound.Document!,options);
        Assert.True(compiled.Accepted,compiled.Diagnostic?.Code+": "+compiled.Diagnostic?.Message);
        return compiled.Compilation!;
    }
    private static decimal Eval(Operation op,string? literal,string? symbol,decimal a,decimal b,int x,int y)=>op switch
    {
        Operation.Literal=>literal switch{"true"=>1,"false"=>0,_=>Number(literal!)},
        Operation.Symbol=>symbol switch{"x"=>x,"y"=>y,_=>throw new InvalidOperationException(symbol)},
        Operation.Add=>a+b,Operation.Subtract=>a-b,Operation.Multiply=>a*b,Operation.Divide=>a/b,
        Operation.Negate=>-a,Operation.Equal=>a==b?1:0,Operation.LessOrEqual=>a<=b?1:0,
        Operation.GreaterOrEqual=>a>=b?1:0,_=>throw new InvalidOperationException(op.ToString())
    };

    [Fact]
    public void GeneratedAffineModelsAgreeAtEveryPointAndUnderPositiveRowScaling()
    {
        // Fixed deterministic grid, not a sample selected after observing results.
        foreach(int a in new[]{-3,-1,0,2})
        foreach(int b in new[]{-2,0,3})
        foreach(int rhs in new[]{-3,0,4})
        foreach(bool maximize in new[]{false,true})
        {
            var direction=maximize?Direction.Maximize:Direction.Minimize;
            var word=maximize?"maximize":"minimize";
            var body=FormattableString.Invariant($"decision x : Int in [-3,3]; decision y : Int in [-3,3]; constraint cap : ({a}*x + {b}*y)/2 <= {rhs}/2; constraint floor : x-y >= -2; {word} goal priority 1 : ({b}*x - {a}*y + 3)/4;");
            var source="problem P { "+body+" }";
            var c=Compile(source);
            var n=c.Normalized;
            var l=ExactLowerer.Lower(n,Manifest,NumericEncoding.Int64,c.GeneratedInputs[c.FactsDigest].ToArray());
            Assert.True(l.Accepted,l.Diagnostic?.Code+": "+l.Diagnostic?.Message);
            var model=l.Model!;
            Assert.Equal(direction,model.Objective!.Direction);
            Assert.Equal(new[]{"x","y"},model.Columns.Select(z=>z.SymbolId));
            Assert.All(model.Columns,z=>{Assert.Equal("-3",z.Lower);Assert.Equal("3",z.Upper);});
            var original=c.Original.Model;
            var nodes=original.Expressions.ToDictionary(z=>z.Id);
            Assert.Equal(original.Expressions.Length,n.Origins.Length);
            foreach(var row in model.Rows)
            {
                Assert.Equal(original.Constraints.Single(z=>z.Id==row.ConstraintId).Span,row.Span);
                Assert.True(Number(row.Scale)>0);
            }
            Assert.Equal(original.Objectives.Single().Span,model.Objective.Span);
            var scaled=Compile(source.Replace($"({a}*x + {b}*y)/2 <= {rhs}/2",$"({a}*x + {b}*y)*2 <= ({rhs})*2",StringComparison.Ordinal));
            var sl=ExactLowerer.Lower(scaled.Normalized,Manifest,NumericEncoding.Int64,scaled.GeneratedInputs[scaled.FactsDigest].ToArray());
            Assert.True(sl.Accepted,sl.Diagnostic?.Message);
            for(int x=-3;x<=3;x++)for(int y=-3;y<=3;y++)
            {
                decimal O(string id)
                {
                    var e=nodes[id];
                    return Eval(e.Op,e.Literal,e.SymbolId,e.Arguments.Length>0?O(e.Arguments[0]):0,e.Arguments.Length>1?O(e.Arguments[1]):0,x,y);
                }
                decimal N(string id)
                {
                    var e=n.Expressions[id];
                    return Eval(e.Operation,e.Literal,e.SymbolId,e.Arguments.Length>0?N(e.Arguments[0]):0,e.Arguments.Length>1?N(e.Arguments[1]):0,x,y);
                }
                bool Row(NativeRow r)
                {
                    var lhs=r.Terms.Sum(t=>Number(t.Value)*(t.Column==0?x:y));
                    var right=Number(r.RightHandSide);
                    return r.Relation switch{Operation.Equal=>lhs==right,Operation.LessOrEqual=>lhs<=right,Operation.GreaterOrEqual=>lhs>=right,_=>throw new InvalidOperationException()};
                }
                bool expected=a*x+b*y<=rhs && x-y>=-2;
                foreach(var r in original.Constraints) Assert.Equal(r.Id=="cap"?a*x+b*y<=rhs:x-y>=-2,O(r.PredicateId)==1);
                foreach(var r in n.Constraints) Assert.Equal(r.Id=="cap"?a*x+b*y<=rhs:x-y>=-2,N(r.PredicateId)==1);
                foreach(var r in model.Rows) Assert.Equal(r.ConstraintId=="cap"?a*x+b*y<=rhs:x-y>=-2,Row(r));
                Assert.Equal(expected,original.Constraints.All(r=>O(r.PredicateId)==1));
                Assert.Equal(expected,n.Constraints.All(r=>N(r.PredicateId)==1));
                Assert.Equal(expected,model.Rows.All(Row));
                Assert.Equal(expected,sl.Model!.Rows.All(Row));
                decimal objective=(b*x-a*y+3)/4m;
                Assert.Equal(objective,O(original.Objectives.Single().ExpressionId));
                Assert.Equal(objective,N(n.Objectives.Single().ExpressionId));
                var g=model.Objective;
                var native=g.Terms.Sum(t=>Number(t.Value)*(t.Column==0?x:y))+Number(g.Offset);
                Assert.Equal(objective,native/Number(g.Scale));
                Assert.Equal(objective,Number(NativeMapping.RestoreObjective(model,native.ToString(CultureInfo.InvariantCulture))));
            }
        }
    }

    [Fact]
    public void NonlinearDiagnosticKeepsOriginalSourceSpan()
    {
        const string source="problem P { decision x : Int in [0,2]; decision y : Int in [0,2]; minimize goal priority 1 : x*y; }";
        var c=Compile(source);
        var l=ExactLowerer.Lower(c.Normalized,Manifest,NumericEncoding.Int64,c.GeneratedInputs[c.FactsDigest].ToArray());
        Assert.False(l.Accepted);
        Assert.Null(l.Model);
        Assert.Equal("lower.nonlinear",l.Diagnostic!.Code);
        Assert.NotNull(l.Diagnostic.Span);
        Assert.Contains(c.Normalized.Origins,o=>o.Span==l.Diagnostic.Span);
        var span=l.Diagnostic.Span!;
        Assert.Equal("audit",span.SourceId);
        Assert.Equal("x*y",source.Substring(span.Start,span.Length));
    }
}
