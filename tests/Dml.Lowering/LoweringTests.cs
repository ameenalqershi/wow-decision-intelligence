using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WowDi.Contracts;
using WowDi.Dml.Binding;
using WowDi.Dml.Lowering;
using WowDi.Dml.Semantics;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class LoweringTests
{
    private static string Hash(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static DmlCompilation Compile(string body,string[]? features=null)
    {
        var source=Encoding.UTF8.GetBytes("problem P { "+body+" }");
        var bound=DmlBinder.Bind("source",source,new BindingContext());
        Assert.True(bound.Accepted,bound.Diagnostic?.Message);
        var options=new DmlCompileOptions("tenant","model","r1",new("shift","UTC","2026-01-01T00:00:00Z","minute"),
            new([new("minute","time","1")],["USD"],["shift"]),[Hash("catalog"u8)],[],[dailyFeature()]);
        if(features is not null)options=options with{RequiredCapabilities=[..features]};
        var result=DmlCompiler.Compile(bound.Document!,options);
        Assert.True(result.Accepted,result.Diagnostic?.Code+": "+result.Diagnostic?.Message);
        return result.Compilation!;
        static string dailyFeature()=>"integer.linear";
    }
    private static SolverCapabilities Manifest()=>new(new("qualification-profile","1","test"),["integer.linear","rational.linear"],
        [ScalarKind.Integer,ScalarKind.Rational,ScalarKind.Boolean],null,null,1,false,true,false);
    private static LoweringResult Lower(DmlCompilation c,NumericEncoding encoding=NumericEncoding.Int64,SolverCapabilities? manifest=null)
        =>ExactLowerer.Lower(c.Normalized,manifest??Manifest(),encoding,c.GeneratedInputs[c.FactsDigest].ToArray());
    private static LoweredModel Valid(DmlCompilation c,NumericEncoding encoding=NumericEncoding.Int64)
    {
        var result=Lower(c,encoding);
        Assert.True(result.Accepted,result.Diagnostic?.Code+": "+result.Diagnostic?.Message);return result.Model!;
    }
    private static BigInteger Integer(string value)=>BigInteger.Parse(value,CultureInfo.InvariantCulture);

    [Fact]
    public void ExactScalingAndFullFiniteEnumerationPreserveRowsAndObjective()
    {
        var c=Compile("decision x : Int in [0,4]; decision y : Int in [0,4]; param p : Int = 2; "+
            "constraint row : (x/3 + y/3) <= 5/3; maximize goal priority 1 : (x/2 + y/2) + 1/2;");
        var lowered=Valid(c);
        Assert.Equal(TransformationKind.Exact,lowered.Transformation);
        Assert.Equal(Hash(c.Original.Write()),lowered.OriginalDigest);
        Assert.Equal(c.FactsDigest,lowered.FactsDigest);
        Assert.Equal(new[]{"x","y"},lowered.Columns.Select(x=>x.SymbolId));
        var row=Assert.Single(lowered.Rows);var goal=lowered.Objective!;
        Assert.Equal("row",row.ConstraintId);Assert.Equal("3",row.Scale);Assert.Equal("5",row.RightHandSide);
        Assert.Equal("2",goal.Scale);Assert.Equal("1",goal.Offset);
        Assert.Equal(c.Original.Model.Constraints[0].Span,row.Span);
        Assert.Equal(c.Original.Model.Objectives[0].Span,goal.Span);
        for(int x=0;x<=4;x++)for(int y=0;y<=4;y++)
        {
            int[] values=[x,y];
            BigInteger lhs=row.Terms.Aggregate(BigInteger.Zero,(n,t)=>n+Integer(t.Value)*values[t.Column]);
            Assert.Equal(x+y<=5,lhs<=Integer(row.RightHandSide));
            var scaled=goal.Terms.Aggregate(Integer(goal.Offset),(n,t)=>n+Integer(t.Value)*values[t.Column]);
            Assert.Equal(x+y+1,scaled);
            var numerator=x+y+1;var expected=numerator%2==0?(numerator/2).ToString(CultureInfo.InvariantCulture):numerator+"/2";
            Assert.Equal(expected,NativeMapping.RestoreObjective(lowered,scaled.ToString(CultureInfo.InvariantCulture)));
            var restored=NativeMapping.RestoreAssignment(lowered,new Dictionary<int,string>{{0,x.ToString()},{1,y.ToString()}});
            Assert.Equal(x.ToString(),restored["x"]);Assert.Equal(y.ToString(),restored["y"]);
        }
    }

    [Fact]
    public void FrozenFactsAreSubstitutedAndUnusedVariablesAndRowsAreNotDropped()
    {
        var c=Compile("decision x : Int in [0,5]; decision unused : Int in [0,1]; param p : Int = 2; "+
            "constraint a : p*x <= 8; constraint b : p*x <= 8; constraint impossible : 2 <= 1; minimize cost priority 1 : p*x;");
        var m=Valid(c);
        Assert.Equal(2,m.Columns.Length);Assert.Equal(3,m.Rows.Length);
        Assert.Equal(new[]{"a","b","impossible"},m.Rows.Select(r=>r.ConstraintId));
        Assert.Equal("2",Assert.Single(m.Rows[0].Terms).Value);
        Assert.Empty(m.Rows[2].Terms);Assert.Equal(Operation.Equal,m.Rows[2].Relation);Assert.Equal("1",m.Rows[2].RightHandSide);
        var replaced=ExactLowerer.Lower(c.Normalized,Manifest(),NumericEncoding.Int64,"{\"p\":\"3\"}"u8.ToArray());
        Assert.Equal("lower.factsDigest",replaced.Diagnostic!.Code);
    }

    [Theory]
    [InlineData("decision x : Int in [0,2]; decision y : Int in [0,2]; minimize a priority 1 : x*y;","lower.nonlinear")]
    [InlineData("decision x : Int in [0,2]; constraint a : x <= 1 soft penalty 2;","lower.soft")]
    [InlineData("decision x : Int in [0,2]; minimize a priority 1 : x; maximize b priority 2 : x;","lower.objective")]
    [InlineData("resource p : Int = 2; scenario s { set p = 1; }","lower.capability")]
    [InlineData("decision x : Int in [0,9223372036854775807];","lower.overflow")]
    [InlineData("decision x : Int in [0,10000000000]; maximize a priority 1 : x*10000000000;","lower.overflow")]
    public void UnsupportedOrUnsafeModelFailsClosed(string body,string code)
    {
        var c=Compile(body);
        Assert.Equal(code,Lower(c).Diagnostic!.Code);
        Assert.Null(Lower(c).Model);
    }

    [Fact]
    public void ManifestRejectsUnsupportedFeaturesDomainsAndCoefficientMagnitude()
    {
        var c=Compile("decision x : Int in [0,10]; constraint a : 3*x <= 9;");
        Assert.Equal("lower.capability",Lower(c,manifest:Manifest() with{Features=[]}).Diagnostic!.Code);
        Assert.Equal("lower.domain",Lower(c,manifest:Manifest() with{Domains=[ScalarKind.Rational]}).Diagnostic!.Code);
        Assert.Equal("lower.overflow",Lower(c,manifest:Manifest() with{MaxCoefficientMagnitude="2"}).Diagnostic!.Code);
        var other=Compile("decision x : Int in [0,1];",["future.feature"]);
        Assert.Equal("lower.profile",Lower(other,manifest:Manifest() with{Features=["future.feature","integer.linear"]}).Diagnostic!.Code);
    }

    [Fact]
    public void ContinuousEncodingRejectsRoundingButAdmitsExactDyadicBounds()
    {
        var c=Compile("decision x : Rational in [0,1/2]; constraint row : x/3 <= 1/3; minimize goal priority 1 : x/3;",["rational.linear"]);
        Assert.Equal("lower.rounding",Lower(c).Diagnostic!.Code);
        var m=Valid(c,NumericEncoding.Binary64ExactInput);
        Assert.Equal("1/2",Assert.Single(m.Columns).Upper);
        Assert.Equal("3",m.Rows[0].Scale);Assert.Equal("1",m.Rows[0].Terms[0].Value);
        var nonDyadic=Compile("decision x : Rational in [0,1/3];",["rational.linear"]);
        Assert.Equal("lower.rounding",Lower(nonDyadic,NumericEncoding.Binary64ExactInput).Diagnostic!.Code);
    }

    [Fact]
    public void BooleanMappingAndConstantRowsRetainExactMeaning()
    {
        var c=Compile("decision enabled : Bool; constraint row : enabled == true; constraint no : true == false;");
        var m=Valid(c);
        Assert.Equal("0",m.Columns[0].Lower);Assert.Equal("1",m.Columns[0].Upper);
        Assert.Equal("true",NativeMapping.RestoreAssignment(m,new Dictionary<int,string>{{0,"1"}})["enabled"]);
        Assert.Equal("false",NativeMapping.RestoreAssignment(m,new Dictionary<int,string>{{0,"0"}})["enabled"]);
        Assert.Equal("lower.map.integrality",Assert.Throws<ContractException>(()=>NativeMapping.RestoreAssignment(m,new Dictionary<int,string>{{0,"2"}})).Code);
        Assert.Equal("lower.map.assignment",Assert.Throws<ContractException>(()=>NativeMapping.RestoreAssignment(m,new Dictionary<int,string>{{1,"1"}})).Code);
        Assert.Empty(m.Rows.Single(r=>r.ConstraintId=="no").Terms);
    }
}
