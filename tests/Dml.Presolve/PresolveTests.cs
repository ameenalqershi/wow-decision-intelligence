using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Contracts;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.Dml.Presolve;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class PresolveTests
{
    private static DmlCompilation Compile(string body,string[]? features=null)
    {
        var binding=DmlBinder.Bind("src",Encoding.UTF8.GetBytes("problem P { "+body+" }"),new BindingContext());
        Assert.True(binding.Accepted,binding.Diagnostic?.Message);
        var options=new DmlCompileOptions("tenant","model","rev",new("shift","UTC","2026-01-01T00:00:00Z","minute"),
            new([new("minute","time","1")],[],["shift"]),[Convert.ToHexStringLower(SHA256.HashData("catalog"u8))],[],[..features??["integer.linear"]]);
        var r=DmlCompiler.Compile(binding.Document!,options);
        Assert.True(r.Accepted,r.Diagnostic?.Message);return r.Compilation!;
    }
    [Fact]
    public void ExhaustiveProjectionAndLiftPreserveFeasibilityAndObjective()
    {
        var c=Compile("decision x : Int in [0,4]; decision y : Int in [0,4]; decision unused : Int in [-2,2]; "+
            "constraint a : x+y <= 5; constraint b : y+x <= 5; constraint tautology : 1 == 1; "+
            "constraint softA : x+y <= 5 soft penalty 2; constraint softB : x+y <= 5 soft penalty 2; "+
            "maximize goal priority 1 : x+y;");
        var p=SafePresolve.Apply(c.Normalized);
        Assert.Equal(new[]{"x","y"},p.Variables.Select(v=>v.Id));
        Assert.Equal(new[]{"a","softA","softB"},p.Constraints.Select(r=>r.Id));
        Assert.Equal(3,p.Ledger.Length);
        Assert.Contains(p.Ledger,e=>e.Kind==RewriteKind.DuplicateHardRow&&e.OriginalId=="b"&&e.RetainedId=="a");
        Assert.Contains(p.Ledger,e=>e.Kind==RewriteKind.TrueHardRow&&e.OriginalId=="tautology");
        Assert.Contains(p.Ledger,e=>e.Kind==RewriteKind.UnusedVariable&&e.OriginalId=="unused"&&e.ReconstructionValue=="-2");
        var feasibleOriginal=new HashSet<(int,int)>();var feasibleProjected=new HashSet<(int,int)>();
        int Eval(string id,IReadOnlyDictionary<string,string> env)
        {
            var e=p.Original.Expressions[id];
            int A()=>Eval(e.Arguments[0],env);int B()=>Eval(e.Arguments[1],env);
            return e.Operation switch{Operation.Literal=>e.Literal=="true"?1:e.Literal=="false"?0:int.Parse(e.Literal!),
                Operation.Symbol=>int.Parse(env[e.SymbolId!]),Operation.Add=>A()+B(),Operation.LessOrEqual=>A()<=B()?1:0,
                Operation.Equal=>A()==B()?1:0,_=>throw new InvalidOperationException()};
        }
        for(int x=0;x<=4;x++)for(int y=0;y<=4;y++)
        {
            for(int u=-2;u<=2;u++)
            {
                var a=new Dictionary<string,string>{{"x",x.ToString()},{"y",y.ToString()},{"unused",u.ToString()}};
                var projected=p.Project(a);
                var before=c.Normalized.Constraints.Where(r=>r.Hard).All(r=>Eval(r.PredicateId,a)==1);
                Assert.Equal(x+y<=5,before);
                if(before)feasibleOriginal.Add((x,y));
                Assert.Equal(x+y,Eval(c.Normalized.Objectives[0].ExpressionId,projected));
                Assert.Equal(c.Normalized.Constraints.Count(r=>!r.Hard&&Eval(r.PredicateId,a)==0),
                    p.Constraints.Count(r=>!r.Hard&&Eval(r.PredicateId,projected)==0));
            }
            var reduced=new Dictionary<string,string>{{"x",x.ToString()},{"y",y.ToString()}};
            bool after=p.Constraints.Where(r=>r.Hard).All(r=>Eval(r.PredicateId,reduced)==1);
            if(after)feasibleProjected.Add((x,y));
            var lifted=p.Restore(reduced);
            Assert.Equal("-2",lifted["unused"]);
            Assert.Equal(after,c.Normalized.Constraints.Where(r=>r.Hard).All(r=>Eval(r.PredicateId,lifted)==1));
            Assert.Equal(x+y,Eval(c.Normalized.Objectives[0].ExpressionId,lifted));
        }
        Assert.True(feasibleOriginal.SetEquals(feasibleProjected));
        Assert.Same(c.Normalized,p.Original);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(c.Original.Write())),p.OriginalDigest);
    }

    [Fact]
    public void ContradictionsRetainBothRowsAndSourceCounterexamples()
    {
        var c=Compile("constraint a : 2 <= 1; constraint b : 2 <= 1; constraint softRow : 2 <= 1 soft penalty 3;");
        var p=SafePresolve.Apply(c.Normalized);
        Assert.True(p.HasConstantContradiction);Assert.Equal(2,p.Contradictions.Length);Assert.Equal(3,p.Constraints.Length);
        Assert.Empty(p.Ledger);
        Assert.All(p.Contradictions,r=>Assert.Equal("false",p.Original.Expressions[r.PredicateId].Literal));
    }

    [Fact]
    public void UnknownFeaturesAndScenariosDoNotAuthorizeElimination()
    {
        var c=Compile("decision x : Int in [0,1]; constraint a : 1 == 1;",["future.feature"]);
        var p=SafePresolve.Apply(c.Normalized);
        Assert.Single(p.Variables);Assert.Single(p.Constraints);Assert.Empty(p.Ledger);
        var s=Compile("resource p : Int = 2; decision x : Int in [0,1]; scenario s { set p = 1; }");
        Assert.Empty(SafePresolve.Apply(s.Normalized).Ledger);
    }

    [Fact]
    public void UnusedBooleanAndRationalWitnessesAreExact()
    {
        var c=Compile("decision flag : Bool; decision amount : Rational in [1/3,2/3];");
        var p=SafePresolve.Apply(c.Normalized);
        Assert.Empty(p.Variables);
        var restored=p.Restore(ImmutableDictionary<string,string>.Empty);
        Assert.Equal("false",restored["flag"]);Assert.Equal("1/3",restored["amount"]);
        Assert.Throws<ContractException>(()=>p.Restore(new Dictionary<string,string>{{"flag","true"}}));
        Assert.Throws<ContractException>(()=>p.Project(ImmutableDictionary<string,string>.Empty));
    }

    [Fact]
    public void EmptyDomainRejectedBeforePresolveAndCancellationDoesNotPublish()
    {
        var c=Compile("decision x : Int in [0,1];");
        var bad=c.Original.Model with{Symbols=[c.Original.Model.Symbols[0] with{Lower="2",Upper="1"}]};
        Assert.Equal("udir.domain",Assert.Throws<ContractException>(()=>UdirDocument.Create(bad)).Code);
        using var token=new CancellationTokenSource();token.Cancel();
        Assert.Throws<OperationCanceledException>(()=>SafePresolve.Apply(c.Normalized,token.Token));
    }
}
