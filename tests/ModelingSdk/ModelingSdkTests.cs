using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.ModelingSdk;
using WowDi.ModelImport;
using WowDi.Samples;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class ModelingSdkTests
{
    private readonly struct Kg : IQuantity;
    private readonly struct Gram : IQuantity;
    private readonly struct Count : IQuantity;
    private readonly struct Usd : IQuantity;
    private readonly struct Eur : IQuantity;
    private readonly struct Minute : IQuantity;
    private readonly struct Instant : IQuantity;
    private readonly struct Hour : IQuantity;
    private static ModelSource Example(string name) => new(name,File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,name+".txt")));
    private static ModelSource Source() => new("source",Encoding.UTF8.GetBytes("قرار model quantity"));
    private static FrozenMeasurementCatalog Catalog() => new(
        [new("kg","mass","1"),new("g","mass","1/1000"),new("minute","time","1"),new("hour","time","60")],
        ["USD","EUR"],["shift"]);
    private static ModelBuilder Builder(ModelSource source) => new("tenant","model","rev",ExampleModels.Basis(),Catalog(),source);
    private static SourceSpan Span(ModelSource s) => s.Locate("quantity");
    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Theory]
    [InlineData("production")]
    [InlineData("workforce")]
    public void RealExamplesBuildAndRoundTrip(string name)
    {
        var source=Example(name);
        var first=name=="production"?ExampleModels.Production(source):ExampleModels.Workforce(source);
        var second=name=="production"?ExampleModels.Production(source):ExampleModels.Workforce(source);
        Assert.Equal(first.Document.Write(),second.Document.Write());
        Assert.Equal(first.Document.Model.InputDigests.ToArray(),second.Document.Model.InputDigests.ToArray());
        Assert.Equal(first.Document.Model.InputDigests.Order(StringComparer.Ordinal),first.Inputs.Keys.Order(StringComparer.Ordinal));
        foreach(var (digest,bytes) in first.Inputs)Assert.Equal(digest,Hash(bytes.AsSpan()));
        Assert.Equal(source.Input.Digest,Assert.Single(first.Document.Model.Sources).Sha256);
        Assert.Equal(3,first.Inputs.Count);
        Assert.NotNull(first.FactsDigest);
        Assert.Contains(first.FactsDigest,first.Inputs.Keys);
        Assert.True(first.Normalize(ExampleModels.Catalog()).Accepted);
        Assert.Null(VersionedModelImport.ValidateSchema(first.Document.Write()));
        var imported=ModelBuilder.Import(first.Document.Write(),ExampleModels.Catalog().Semantics);
        Assert.Equal(first.Document.Write(),imported.Write());
        var m=imported.Model;
        Assert.Single(m.Constraints);Assert.True(m.Constraints[0].Hard);
        Assert.Single(m.Objectives);
        Assert.Equal(name=="production"?Direction.Maximize:Direction.Minimize,m.Objectives[0].Direction);
        Assert.Equal(name=="production"?3:0,m.Scenarios.Length);
        Assert.All(m.Expressions,e=>Assert.True(e.Span.Length>0&&e.Span.Start+e.Span.Length<=source.Document.Utf16Length));
        Assert.Contains(m.Expressions,e=>e.Op==Operation.Multiply);
        var symbol=m.Symbols.Single(s=>s.Role==SymbolRole.Variable);
        Assert.Equal("0",symbol.Lower);Assert.Equal(name=="production"?"8":"12",symbol.Upper);
        if(name=="production")Assert.Equal("4",m.Scenarios.Single(s=>s.Id=="shortage").Overrides[0].Value);
    }

    [Fact]
    public void FrozenCatalogIsCanonicalAndOwnsInput()
    {
        UnitDefinition[] defs=[new("minute","time","1"),new("kg","mass","1")];
        var first=new FrozenMeasurementCatalog(defs,["USD","EUR"],["shift"]);
        var other=new FrozenMeasurementCatalog(defs.Reverse(),["EUR","USD"],["shift"]);
        Assert.Equal(first.Input.Digest,other.Input.Digest);
        defs[0]=new("minute","time","2");
        Assert.Equal(first.Input.Digest,other.Input.Digest);
        Assert.Equal("1",first.Semantics.ConvertUnit("1","minute","minute"));
        Assert.NotEqual(first.Input.Digest,new FrozenMeasurementCatalog(defs,["USD","EUR"],["shift"]).Input.Digest);
    }

    [Fact]
    public void ExactInputsAreReducedWithoutFloatingPoint()
    {
        Assert.Equal("7/3",new ExactValue(14,6).ToString());
        Assert.Equal("-1/10",ExactValue.FromDecimal(-0.1m).ToString());
        Assert.Equal("0",default(ExactValue).ToString());
        Assert.Equal("9223372036854775808",new ExactValue(BigInteger.One<<63,1).ToString());
        Assert.Throws<ArgumentOutOfRangeException>(()=>new ExactValue(1,0));
        var s=Source();var b=Builder(s);var k=b.Rational<Kg>("kg","kg");var v=b.Literal(k,new ExactValue(7,3),Span(s));
        b.Optimize("objective",v,Direction.Minimize,1,Span(s),new ExactValue(2,3),ExactValue.FromDecimal(0.01m));
        var doc=b.Build().Document;
        Assert.Equal("7/3",Assert.Single(doc.Model.Expressions).Literal);
        Assert.Equal("2/3",doc.Model.Objectives[0].Weight);
        Assert.Equal("1/100",doc.Model.Objectives[0].Tolerance);
    }

    [Fact]
    public void ExplicitConversionsDelegateAndPreserveFxReceipt()
    {
        var s=Source();var b=Builder(s);var g=b.Integer<Gram>("g","g");var k=b.Rational<Kg>("kg","kg");
        var converted=b.ConvertedLiteral(g,k,1250,Span(s));
        var usd=b.Money<Usd>("usd","USD");var eur=b.Money<Eur>("eur","EUR");
        var start=DateTimeOffset.Parse("2026-01-01T00:00:00Z",System.Globalization.CultureInfo.InvariantCulture);
        var fx=new FxSnapshot("fx-v1","EUR","USD","5/4",start,start.AddDays(1));
        var money=b.CurrencyLiteral(eur,usd,4,fx,start,Span(s));
        Assert.Equal("measure.fx",Assert.Throws<ContractException>(()=>b.CurrencyLiteral(eur,usd,4,fx,start.AddDays(1),Span(s))).Code);
        b.Optimize("money",money,Direction.Maximize,1,Span(s));
        b.Optimize("quantity",converted,Direction.Maximize,2,Span(s));
        var model=b.Build();
        Assert.Equal("5/4",model.Document.Model.Expressions.Single(e=>e.Id==converted.Id).Literal);
        Assert.Equal("5",model.Document.Model.Expressions.Single(e=>e.Id==money.Id).Literal);
        Assert.Equal(3,model.Inputs.Count);
        Assert.Contains(model.Inputs.Values,v=>Encoding.UTF8.GetString(v.AsSpan()).Contains("fx-v1",StringComparison.Ordinal));
    }

    [Fact]
    public void CrossBuilderHandlesFailWithoutAppendingNodes()
    {
        var s=Source();var a=Builder(s);var b=Builder(s);var ka=a.Integer<Kg>("kg","kg");var kb=b.Integer<Kg>("kg","kg");
        var x=a.Literal(ka,1,Span(s));var y=b.Literal(kb,1,Span(s));
        Assert.Equal("sdk.foreignHandle",Assert.Throws<ContractException>(()=>a.Add(x,y,Span(s))).Code);
        Assert.Equal("sdk.foreignHandle",Assert.Throws<ContractException>(()=>a.Literal(kb,1,Span(s))).Code);
        Assert.Single(a.Build().Document.Model.Expressions);
    }

    [Fact]
    public void InvalidDimensionsAreRejectedByExistingMeasurementAdmission()
    {
        var s=Source();var b=Builder(s);var k=b.Integer<Kg>("kg","kg");var count=b.Integer<Count>("count");
        var wrong=b.Multiply(b.Literal(k,2,Span(s)),b.Literal(k,3,Span(s)),count,Span(s));
        b.Optimize("x",wrong,Direction.Minimize,1,Span(s));
        Assert.Equal("measure.expression",Assert.Throws<ContractException>(()=>b.Build()).Code);
    }

    [Theory]
    [InlineData("fractionalInteger")]
    [InlineData("reversedBounds")]
    [InlineData("duplicateId")]
    [InlineData("duplicatePriority")]
    [InlineData("badSpan")]
    [InlineData("badPenalty")]
    [InlineData("variableOverride")]
    [InlineData("oversizedNumber")]
    public void InvalidDomainUsesUdirDiagnostics(string mutation)
    {
        var s=Source();var b=Builder(s);var k=b.Integer<Kg>("kg","kg");var span=Span(s);
        var symbol=b.Variable("q",k,mutation=="reversedBounds"?9:0,8,span);
        var expr=b.Reference(symbol,span);
        switch(mutation)
        {
            case "fractionalInteger":b.Literal(k,new ExactValue(1,3),span);break;
            case "duplicateId":b.Variable("q",k,0,1,span);break;
            case "duplicatePriority":b.Optimize("a",expr,Direction.Minimize,1,span);b.Optimize("b",expr,Direction.Maximize,1,span);break;
            case "badSpan":b.Literal(k,1,new("source",999,1));break;
            case "badPenalty":b.SoftConstraint("c",b.Literal(true,span),0,span);break;
            case "variableOverride":b.AddScenario("root",0,"root",span,null,null,b.Override(symbol,1));break;
            case "oversizedNumber":b.Literal(k,new ExactValue(BigInteger.Pow(10,256),1),span);break;
        }
        Assert.StartsWith("udir.",Assert.Throws<ContractException>(()=>b.Build()).Code);
    }

    [Fact]
    public void ExactTemporalOperationsAndBooleanConstraint()
    {
        var s=Source();var b=Builder(s);var time=b.Instant<Instant>("instant","minute");var duration=b.Duration<Minute>("elapsed","minute");
        var start=b.Literal(time,10,Span(s));var shift=b.Literal(duration,2,Span(s));
        var end=b.Shift(start,shift,Span(s));
        var elapsed=b.Difference(end,start,duration,Span(s));
        var flag=b.BooleanVariable("enabled",Span(s));
        b.HardConstraint("enabledConstraint",b.Equal(b.Reference(flag,Span(s)),b.Literal(true,Span(s)),Span(s)),Span(s));
        b.Optimize("duration",elapsed,Direction.Minimize,1,Span(s));
        var model=b.Build().Document.Model;
        Assert.Equal(Operation.Subtract,model.Expressions.Single(e=>e.Id==elapsed.Id).Op);
        Assert.Equal("elapsed",model.Expressions.Single(e=>e.Id==elapsed.Id).TypeId);
    }

    [Fact]
    public void BuildSealsRevisionAndSourceBytesAreDefensivelyCopied()
    {
        var bytes=Encoding.UTF8.GetBytes("قرار quantity");
        var s=new ModelSource("source",bytes);bytes[0]=0;
        Assert.Equal(Hash(s.Input.Bytes.AsSpan()),s.Document.Sha256);
        Assert.Equal(5,s.Locate("quantity").Start);
        Assert.Throws<ArgumentException>(()=>s.Locate("missing"));
        Assert.Throws<ArgumentOutOfRangeException>(()=>s.Span(999,1));
        var b=Builder(s);var k=b.Integer<Kg>("kg","kg");b.Literal(k,1,Span(s));
        var frozen=b.Build();
        Assert.Same(frozen,b.Build());
        Assert.Equal("sdk.frozen",Assert.Throws<ContractException>(()=>b.Literal(k,2,Span(s))).Code);
        Assert.Equal("sdk.frozen",Assert.Throws<ContractException>(()=>b.AddInput(new FrozenInput(new byte[]{1}))).Code);
        Assert.Single(frozen.Document.Model.Expressions);
    }


    [Fact]
    public void InvalidMeasurementInputsAndTypeRegistrationsAreRejected()
    {
        var source=Source();var b=Builder(source);var kg=b.Integer<Kg>("kg","kg");
        Assert.Equal("sdk.typeRegistration",Assert.Throws<ContractException>(()=>b.Rational<Kg>("kg2","kg")).Code);
        Assert.Equal("sdk.typeRegistration",Assert.Throws<ContractException>(()=>b.Integer<Count>("kg")).Code);
        var hours=b.Duration<Hour>("hour","hour");
        Assert.Equal("measure.dimension",Assert.Throws<ContractException>(()=>b.ConvertedLiteral(kg,hours,1,Span(source))).Code);
        var usd=b.Money<Usd>("usd","USD");var eur=b.Money<Eur>("eur","EUR");
        Assert.Equal("sdk.conversionType",Assert.Throws<ContractException>(()=>b.ConvertedLiteral(usd,eur,1,Span(source))).Code);
        Assert.Empty(b.Build().Document.Model.Expressions);
        var invalid=Builder(source);
        var bad=invalid.Money<Usd>("unsupported","JPY");
        invalid.Literal(bad,1,Span(source));
        Assert.Equal("measure.currency",Assert.Throws<ContractException>(()=>invalid.Build()).Code);
    }

    [Fact]
    public void ScenarioOverridesRetainExactValuesAndBooleanTypes()
    {
        var source=Source();var b=Builder(source);var kg=b.Rational<Kg>("kg","kg");
        var p=b.Parameter("amount",kg,0,10,Span(source));
        var available=b.BooleanParameter("available",Span(source));
        b.AddScenario("root",0,"before",Span(source));
        b.AddScenario("child",1,"after",Span(source),"root",new ExactValue(1,2),
            b.Override(p,new ExactValue(7,3)),b.Override(available,false));
        var model=b.Build().Document.Model;
        var child=model.Scenarios.Single(s=>s.Id=="child");
        Assert.Equal("1/2",child.Probability);
        Assert.Equal("7/3",child.Overrides[0].Value);
        Assert.Equal("false",child.Overrides[1].Value);
    }

    [Fact]
    public void SourceValidationRejectsAmbiguousTextAndInvalidUtf8()
    {
        var source=new ModelSource("s",Encoding.UTF8.GetBytes("same same"));
        Assert.Throws<ArgumentException>(()=>source.Locate("same"));
        Assert.Throws<DecoderFallbackException>(()=>new ModelSource("bad",new byte[]{0xff}));
    }

    [Fact]
    public void ExplicitFrozenFactsUseExactDomainAndDoNotReplaceModel()
    {
        var source=Source();var b=Builder(source);var kg=b.Rational<Kg>("kg","kg");
        var stock=b.Resource("stock",kg,0,10,Span(source));
        b.Parameter("fixed",kg,2,Span(source));
        var flag=b.BooleanParameter("available",Span(source));
        b.FreezeFacts(b.Fact(stock,new ExactValue(7,3)),b.Fact(flag,true));
        var result=b.Build();Assert.NotNull(result.FactsDigest);
        var facts=JsonSerializer.Deserialize<Dictionary<string,string>>(result.Inputs[result.FactsDigest!].AsSpan())!;
        Assert.Equal("7/3",facts["stock"]);Assert.Equal("2",facts["fixed"]);Assert.Equal("true",facts["available"]);
        Assert.Equal("0",result.Document.Model.Symbols.Single(s=>s.Id=="stock").Lower);
        Assert.Equal("10",result.Document.Model.Symbols.Single(s=>s.Id=="stock").Upper);
        Assert.Same(result,b.Build());
        Assert.Equal("sdk.frozen",Assert.Throws<ContractException>(()=>b.FreezeFacts()).Code);
    }

    [Theory]
    [InlineData("missing","sdk.missingFact")]
    [InlineData("outside","sdk.factDomain")]
    [InlineData("fraction","sdk.factDomain")]
    [InlineData("variable","sdk.variableFact")]
    [InlineData("huge","sdk.factDomain")]
    public void InvalidFactSnapshotsFailBeforePublication(string mutation,string code)
    {
        var source=Source();var b=Builder(source);var kg=b.Integer<Kg>("kg","kg");
        var stock=b.Resource("stock",kg,0,10,Span(source));
        var q=b.Variable("q",kg,0,10,Span(source));
        switch(mutation)
        {
            case "missing":b.FreezeFacts();break;
            case "outside":b.FreezeFacts(b.Fact(stock,11));break;
            case "fraction":b.FreezeFacts(b.Fact(stock,new ExactValue(1,2)));break;
            case "variable":b.FreezeFacts(b.Fact(stock,2),b.Fact(q,1));break;
            case "huge":b.FreezeFacts(b.Fact(stock,new ExactValue(BigInteger.Pow(10,256),1)));break;
        }
        Assert.Equal(code,Assert.Throws<ContractException>(()=>b.Build()).Code);
    }

    [Fact]
    public void FactOwnershipAndDuplicateBindingFailWithoutMutatingStagedSnapshot()
    {
        var source=Source();var a=Builder(source);var b=Builder(source);
        var ak=a.Integer<Kg>("kg","kg");var bk=b.Integer<Kg>("kg","kg");
        var x=a.Resource("x",ak,0,10,Span(source));var y=b.Resource("y",bk,0,10,Span(source));
        Assert.Equal("sdk.foreignHandle",Assert.Throws<ContractException>(()=>a.Fact(y,1)).Code);
        Assert.Equal("sdk.foreignHandle",Assert.Throws<ContractException>(()=>a.FreezeFacts(b.Fact(y,1))).Code);
        Assert.Equal("sdk.duplicateFact",Assert.Throws<ContractException>(()=>a.FreezeFacts(a.Fact(x,1),a.Fact(x,2))).Code);
        a.FreezeFacts(a.Fact(x,3));Assert.NotNull(a.Build().FactsDigest);
    }

    [Fact]
    public void SharedNormalizerRejectsUnsafeExpressionAndWrongCatalog()
    {
        var source=Source();var b=Builder(source);var r=b.Rational<Kg>("kg","kg");
        var v=b.Variable("q",r,-1,1,Span(source));var dimensionless=b.Rational<Count>("count");
        var bad=b.Divide(b.Literal(r,1,Span(source)),b.Reference(v,Span(source)),dimensionless,Span(source));
        b.Optimize("goal",bad,Direction.Minimize,1,Span(source));
        var built=b.Build();
        Assert.Equal("DML-DIV-001",built.Normalize(Catalog()).Diagnostic!.Code);
        var different=new FrozenMeasurementCatalog([new("minute","time","1"),new("kg","mass","2")],["USD"],["shift"]);
        Assert.Equal("sdk.catalog",Assert.Throws<ContractException>(()=>built.Normalize(different)).Code);
    }

    [Fact]
    public void ImportPreservesFixtureButRequiresMeasurementAdmission()
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"));
        var catalog=new MeasurementSemantics([new("minute","time","1")],["USD"],["shift-a"]);
        var doc=ModelBuilder.Import(bytes,catalog);
        Assert.Equal(UdirDocument.Read(bytes).Write(),doc.Write());
        Assert.NotNull(doc.Extensions);
        var wrong=new MeasurementSemantics([new("minute","time","1")],["EUR"],["shift-a"]);
        Assert.StartsWith("measure.",Assert.Throws<ContractException>(()=>ModelBuilder.Import(bytes,wrong)).Code);
        Assert.Equal("envelope.json",Assert.Throws<ContractException>(()=>ModelBuilder.Import(new byte[]{1},catalog)).Code);
    }
}
