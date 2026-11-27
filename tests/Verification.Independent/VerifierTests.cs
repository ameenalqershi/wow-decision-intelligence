using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Verification.Independent.Tests;

public sealed class VerifierTests
{
    internal static Model Model()=>UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","factory.json"))).Model;
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static VerificationScope Scope(UdirDocument doc)=>new(doc.Model.TenantId,doc.Model.ModelId,doc.Model.RevisionId,"run",Hash(doc.Write()));
    private static MeasurementSemantics Catalog()=>new([new("minute","time","60")],["USD"],["shift-a"]);
    private static byte[] Facts()=>Encoding.UTF8.GetBytes("{\"available\":\"8\"}");
    private static UdirDocument Document()=>UdirDocument.Create(Model() with{Scenarios=[],Requires=["integer.linear"],InputDigests=[Hash(Facts())]});
    private static SolverResult Candidate(string x,string value)=>new("fixture","factory","r17","run",new("fixture","1","test"),
        MathematicalStatus.Optimal,Termination.Timeout,ImmutableDictionary<string,string>.Empty.Add("x",x),
        [new("revenue",value,null,"0","0")],null,new(null,null,null));

    [Fact]
    public void ExhaustiveOriginalCapacityAndExactCost()
    {
        var doc=Document();
        for(int x=-1;x<=13;x++)
        {
            int n=x*7;int gcd=(n%3==0)?3:1;
            var expected=gcd==3?(n/3).ToString():$"{n}/3";
            var result=OriginalVerifier.Check(doc,Catalog(),Scope(doc),Candidate(x.ToString(),expected),Facts());
            Assert.Equal(x>=0&&x<=8,result.Accepted);
            if(x>=0&&x<=12)Assert.Equal(expected,Assert.Single(result.Objectives).ExactValue);
        }
    }

    [Theory]
    [InlineData("1/2","7/6","verify.integrality")]
    [InlineData("3","8","verify.objectiveMismatch")]
    [InlineData("9","21","verify.hardConstraint")]
    public void CounterexamplesReject(string x,string cost,string code)
    {
        var doc=Document();
        var result=OriginalVerifier.Check(doc,Catalog(),Scope(doc),Candidate(x,cost),Facts());
        Assert.False(result.Accepted);
        Assert.Contains(result.Diagnostics,d=>d.Code==code);
    }

    [Fact]
    public void FrozenFactsCannotBeReplacedBySolverOrStaleSource()
    {
        var doc=Document();
        var report=Candidate("3","7");
        Assert.Equal("verify.factsDigest",Assert.Single(OriginalVerifier.Check(doc,Catalog(),Scope(doc),report,Encoding.UTF8.GetBytes("{\"available\":\"12\"}")).Diagnostics).Code);
        Assert.Contains(OriginalVerifier.Check(doc,Catalog(),Scope(doc),report with{Incumbent=report.Incumbent!.Add("available","12")},Facts()).Diagnostics,d=>d.Code=="verify.extraBinding");
        Assert.Equal("verify.scope",Assert.Single(OriginalVerifier.Check(doc,Catalog(),Scope(doc),report with{TenantId="other"},Facts()).Diagnostics).Code);
        Assert.Equal(VerificationStatus.NoCandidate,OriginalVerifier.Check(doc,Catalog(),Scope(doc),report with{Incumbent=null},Facts()).Status);
        Assert.Equal(VerificationStatus.Unsupported,OriginalVerifier.Check(UdirDocument.Create(Model()),Catalog(),Scope(UdirDocument.Create(Model())),report,Facts()).Status);
    }

    [Fact]
    public void SoftRowsAndEveryObjectiveAreReportedSeparately()
    {
        var original=Document().Model;
        var model=UdirDocument.Create(original with{Constraints=[original.Constraints[0] with{Hard=false,Penalty="2"}],
            Objectives=[original.Objectives[0],original.Objectives[0] with{Id="secondary",Priority=2,Direction=Direction.Minimize}]});
        var report=Candidate("9","21") with{Objectives=[new("secondary","21",null,"0","0"),new("revenue","21",null,"0","0")]};
        var result=OriginalVerifier.Check(model,Catalog(),Scope(model),report,Facts());
        Assert.True(result.Accepted);
        Assert.Equal("2",Assert.Single(result.Constraints).ViolatedPenalty);
        Assert.Equal(new[]{"revenue","secondary"},result.Objectives.Select(x=>x.Id));
        Assert.All(result.Objectives,x=>Assert.Equal("USD",x.Currency));
    }
}
