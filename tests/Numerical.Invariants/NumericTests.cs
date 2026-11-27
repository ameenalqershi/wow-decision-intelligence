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
using WowDi.Verification.Independent;
using WowDi.Verification.Bounds;
using Xunit;

namespace WowDi.Tests;

public sealed class NumericTests
{
    private static readonly byte[] CatalogBytes="""{"units":[{"symbol":"minute","dimension":"time","scale":"1"},{"symbol":"kg","dimension":"mass","scale":"1"},{"symbol":"g","dimension":"mass","scale":"1/1000"}],"currencies":["USD","EUR"],"calendars":["shift"]}"""u8.ToArray();
    private static MeasurementSemantics Catalog()=>new([new("minute","time","1"),new("kg","mass","1"),new("g","mass","1/1000")],["USD","EUR"],["shift"]);
    private static string Hash(ReadOnlySpan<byte> b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    private static string S(int n)=>n.ToString(CultureInfo.InvariantCulture);
    private static readonly SolverIdentity Oracle=new("enumeration-fixture","1","managed");
    private static SolverCapabilities Manifest()=>new(Oracle,["integer.linear","rational.linear"],
        [ScalarKind.Integer,ScalarKind.Rational,ScalarKind.Boolean],null,null,1,false,true,true);
    private static DmlCompilation Compile(string body,string capability="integer.linear")
    {
        var b=DmlBinder.Bind("src",Encoding.UTF8.GetBytes("problem P { "+body+" }"),new BindingContext());
        Assert.True(b.Accepted,b.Diagnostic?.Message);
        var c=DmlCompiler.Compile(b.Document!,new("tenant","model","r1",new("shift","UTC","2026-01-01T00:00:00Z","minute"),Catalog(),[Hash(CatalogBytes)],[],[capability]));
        Assert.True(c.Accepted,c.Diagnostic?.Code+": "+c.Diagnostic?.Message);
        return c.Compilation!;
    }
    private static VerificationScope Scope(DmlCompilation c)=>new("tenant","model","r1","enumeration",Hash(c.Original.Write()));
    private static SolverResult Witness(string x,string value,string abs="0",string rel="0")=>new("tenant","model","r1","enumeration",Oracle,
        MathematicalStatus.Feasible,Termination.Completed,ImmutableDictionary<string,string>.Empty.Add("x",x),
        [new("goal",value,null,abs,rel)],null,new(null,null,null));
    private static VerificationReport Check(DmlCompilation c,SolverResult r)=>OriginalVerifier.Check(c.Original,Catalog(),Scope(c),r,c.GeneratedInputs[c.FactsDigest].ToArray());

    [Fact]
    public void EquivalentMassScalingsPreserveEveryIntegerAssignmentAndPhysicalCost()
    {
        var kg=Compile("decision x : Int in [0,8]; param capacity : Int[kg] = 10[kg]; param usage : Int[kg] = 2[kg]; constraint row : x*usage <= capacity; maximize goal priority 1 : x*usage;");
        var g=Compile("decision x : Int in [0,8]; param capacity : Int[g] = 10000[g]; param usage : Int[g] = 2000[g]; constraint row : x*usage <= capacity; maximize goal priority 1 : x*usage;");
        for(int x=0;x<=8;x++)
        {
            var a=Check(kg,Witness(S(x),S(2*x)));var b=Check(g,Witness(S(x),S(2000*x)));
            Assert.Equal(x<=5,a.Accepted);Assert.Equal(a.Accepted,b.Accepted);
            Assert.Equal(Assert.Single(a.Objectives).ExactValue,Catalog().ConvertUnit(Assert.Single(b.Objectives).ExactValue,"g","kg"));
        }
        Assert.NotEqual(kg.Normalized.SemanticDigest,g.Normalized.SemanticDigest); // no silent physical-unit identity
    }

    [Theory]
    [InlineData("499999/1000000",true)]
    [InlineData("1/2",true)]
    [InlineData("500001/1000000",false)]
    public void ExactResidualAtBoundaryCannotBeHiddenBySolverTolerance(string x,bool expected)
    {
        // Numeric-domain test using the currently admitted S04 profile; not a qualified continuous adapter.
        var c=Compile("decision x : Rational in [0,1]; constraint row : x <= 1/2; minimize goal priority 1 : x;");
        var report=Check(c,Witness(x,x,"1/100","1/100"));
        Assert.Equal(expected,report.Accepted);
        Assert.Equal(expected,Assert.Single(report.Constraints).Satisfied);
        if(!expected)Assert.Contains(report.Diagnostics,d=>d.Code=="verify.hardConstraint"&&d.Span is not null);
    }

    [Fact]
    public void ApproximateObjectiveIsNotAnExactCertificate()
    {
        var c=Compile("decision x : Int in [0,4]; minimize goal priority 1 : x/3;");
        Assert.True(Check(c,Witness("1","1/3")).Accepted);
        var report=Check(c,Witness("1","333333/1000000","1/100","1/100"));
        Assert.False(report.Accepted);
        Assert.Contains(report.Diagnostics,d=>d.Code=="verify.objectiveMismatch"&&d.Span is not null);
        Assert.Equal("1/3",Assert.Single(report.Objectives).ExactValue);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e309")]
    public void NonFiniteOrNoncanonicalNativeWitnessIsRejected(string raw)
    {
        var c=Compile("decision x : Rational in [0,1]; minimize goal priority 1 : x;");
        var report=Check(c,Witness(raw,"0"));
        Assert.False(report.Accepted);
        Assert.Contains(report.Diagnostics,d=>d.OriginalId=="x"&&d.Span is not null);
    }

    [Fact]
    public void ExactConversionBoundariesAndFxEffectiveInterval()
    {
        Assert.Equal(long.MaxValue,MeasurementSemantics.ToInt64Exact("9223372036854775807"));
        Assert.Equal(long.MinValue,MeasurementSemantics.ToInt64Exact("-9223372036854775808"));
        Assert.Equal("measure.overflow",Assert.Throws<ContractException>(()=>MeasurementSemantics.ToInt64Exact("9223372036854775808")).Code);
        Assert.Equal("measure.fraction",Assert.Throws<ContractException>(()=>MeasurementSemantics.ToInt64Exact("1/3")).Code);
        var start=DateTimeOffset.Parse("2026-01-01T00:00:00Z",CultureInfo.InvariantCulture);
        var fx=new FxSnapshot("v1","EUR","USD","5/4",start,start.AddDays(1));
        Assert.Equal("5/12",Catalog().ConvertCurrency("1/3","EUR","USD",fx,start).Amount);
        Assert.Equal("measure.fx",Assert.Throws<ContractException>(()=>Catalog().ConvertCurrency("1","EUR","USD",fx,start.AddDays(1))).Code);
        Assert.Equal("measure.dimension",Assert.Throws<ContractException>(()=>Catalog().ConvertUnit("1","EUR","USD")).Code);
    }

    [Theory]
    [InlineData("9007199254740991",true)]
    [InlineData("9007199254740992",false)]
    public void Binary64ConservativeBoundaryIsExplicit(string upper,bool accepted)
    {
        var c=Compile("decision x : Rational in [0,"+upper+"];","rational.linear");
        var result=ExactLowerer.Lower(c.Normalized,Manifest(),NumericEncoding.Binary64ExactInput,c.GeneratedInputs[c.FactsDigest].ToArray());
        Assert.Equal(accepted,result.Accepted);
        if(!accepted){Assert.Equal("lower.overflow",result.Diagnostic!.Code);Assert.NotNull(result.Diagnostic.Span);}
    }

    [Fact]
    public void ExactInputStillRequiresIndependentContinuousProfileAdmission()
    {
        var c=Compile("decision x : Rational in [0,1/2]; minimize goal priority 1 : x;","rational.linear");
        var lower=ExactLowerer.Lower(c.Normalized,Manifest(),NumericEncoding.Binary64ExactInput,c.GeneratedInputs[c.FactsDigest].ToArray());
        Assert.True(lower.Accepted);
        var report=Check(c,Witness("1/2","1/2"));
        Assert.Equal(VerificationStatus.Unsupported,report.Status);
        Assert.Contains(report.Diagnostics,d=>d.Code=="verify.unsupportedProfile");
        // Explicit interoperability gate, not authorization to remove the required capability.
    }

    [Fact]
    public void RepeatExactEnumerationAndManagedProjectionAreDeterministic()
    {
        const string body="decision x : Int in [0,8]; constraint row : x <= 5; maximize goal priority 1 : x/3;";
        var c=Compile(body);
        var binding=new EvidenceBinding(Scope(c),c.FactsDigest,Hash(CatalogBytes));
        var candidate=Witness("5","5/3");
        var facts=c.GeneratedInputs[c.FactsDigest].ToArray();
        var first=EvidenceClassifier.Assess(c.Original,binding,CatalogBytes,facts,new(binding,candidate),Manifest(),enumerate:true);
        Assert.False(first.Rejected,first.Diagnostic);
        Assert.Equal(EvidenceStrength.ExactEnumeration,first.Strength);
        Assert.Equal(9,first.Proof!.Checked);Assert.Equal(6,first.Proof.Feasible);Assert.Equal("5/3",first.Proof.Optimum);
        for(int i=0;i<8;i++)
        {
            var repeat=Compile(body);
            Assert.Equal(c.Original.Write(),repeat.Original.Write());
            Assert.Equal(c.Normalized.CanonicalBytes.ToArray(),repeat.Normalized.CanonicalBytes.ToArray());
            var r=EvidenceClassifier.Assess(repeat.Original,binding,CatalogBytes,facts,new(binding,candidate),Manifest(),enumerate:true);
            Assert.Equal(first.Status,r.Status);Assert.Equal(first.Strength,r.Strength);Assert.Equal(first.Proof.Optimum,r.Proof!.Optimum);
            Assert.Equal(first.Proof.Witness!.OrderBy(p=>p.Key),r.Proof.Witness!.OrderBy(p=>p.Key));
        }
    }
}
