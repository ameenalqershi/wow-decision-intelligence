using System.Collections.Immutable;
using System.Text.Json;
using WowDi.Udir;
using WowDi.Manufacturing.Data;
using Xunit;

namespace WowDi.Verification.Independent.Tests;

public sealed class PilotTests
{
    private static MeasurementSemantics Catalog()=>new([new("minute","time","60"),new("unit","count","1"),new("piece","count","1"),new("kg","mass","1")],["USD"],["shift-a","factory-clock"]);
    private static FactoryDataset Source(bool outage=false)=>new(FactoryAdmission.Profile,"r17",
        new("shift-a","UTC","2026-01-01T00:00:00Z","minute",10),"USD",
        [new("M",outage?[new(1,3)]:[])],[new("stock","unit","1")],[new("R1","stock","unit","1",3)],
        [new("A","job-a","M",0,3,4,"3",[new("stock","unit","1")]),
         new("B","job-b","M",0,2,2,"1",[new("stock","unit","1")])],[],"USD");
    private static (UdirDocument Doc,VerificationScope Scope,byte[] Bytes) Freeze(FactoryDataset source)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(source,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        var doc=UdirDocument.Create(VerifierTests.Model() with{InputDigests=[VerifierTests.Hash(bytes)],Scenarios=[],Requires=[]});
        return (doc,VerifierTests.Scope(doc),bytes);
    }
    private static PilotVerification Check(FactoryDataset source,int a,int b,string cost)
    {
        var (doc,scope,bytes)=Freeze(source);
        return PilotVerifier.Check(doc,Catalog(),scope,bytes,new(scope,ImmutableDictionary<string,string>.Empty.Add("A",a.ToString()).Add("B",b.ToString()),cost));
    }

    [Theory]
    [InlineData(false,3)]
    [InlineData(true,12)]
    public void ExhaustiveStartPairsMatchTickByTickOracle(bool outage,int expectedMin)
    {
        var source=Source(outage);
        var (doc,scope,bytes)=Freeze(source);
        var feasible=new List<int>();
        for(int a=0;a<=10;a++)for(int b=0;b<=10;b++)
        {
            bool valid=a+3<=10&&b+2<=10;
            for(int t=0;t<10;t++)
            {
                int busy=(a<=t&&t<a+3?1:0)+(b<=t&&t<b+2?1:0);
                if(busy>1||outage&&t>=1&&t<3&&busy>0)valid=false;
                if((a<=t?1:0)+(b<=t?1:0)>(t>=3?2:1))valid=false;
            }
            int cost=3*Math.Max(0,a+3-4)+Math.Max(0,b+2-2);
            var result=PilotVerifier.Check(doc,Catalog(),scope,bytes,new(scope,ImmutableDictionary<string,string>.Empty.Add("A",a.ToString()).Add("B",b.ToString()),cost.ToString()));
            Assert.Equal(valid,result.Status==VerificationStatus.VerifiedFeasible);
            if(valid){feasible.Add(cost);Assert.Equal(cost.ToString(),result.ExactCost);}
        }
        Assert.Equal(expectedMin,feasible.Min());
    }

    [Fact]
    public void HalfOpenBoundariesAndReceiptAtStartAreValidButEarlyConsumptionIsNot()
    {
        Assert.Equal(VerificationStatus.VerifiedFeasible,Check(Source(),0,3,"3").Status);
        var source=Source() with{Calendars=[new("M",[]),new("N",[])]};
        source=source with{Operations=source.Operations.SetItem(1,source.Operations[1] with{MachineId="N"})};
        Assert.Contains(Check(source,0,0,"0").Diagnostics,x=>x.Code=="pilot.materialPrefix");
        Assert.Equal(VerificationStatus.VerifiedFeasible,Check(source,0,3,"3").Status);
        Assert.Contains(Check(Source(),0,3,"4").Diagnostics,x=>x.Code=="pilot.objectiveMismatch");
    }

    [Fact]
    public void PrecedenceHardDueAndCalendarAreChecked()
    {
        var source=Source() with{Precedence=[new("B","A")]};
        Assert.Contains(Check(source,0,3,"3").Diagnostics,x=>x.Code=="pilot.precedence");
        source=Source() with{Operations=Source().Operations.SetItem(0,Source().Operations[0] with{HardDeadlineMinute=2})};
        Assert.Contains(Check(source,0,3,"3").Diagnostics,x=>x.Code=="pilot.hardDeadline");
        source=Source() with{Calendars=[new("M",[new(1,4),new(3,5)])]};
        Assert.Contains(Check(source,0,3,"3").Diagnostics,x=>x.Code.StartsWith("pilot.source.",StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("baseline",0,3,null,"3",true)]
    [InlineData("outage",3,6,null,"12",true)]
    [InlineData("multi",0,4,"6","1/2",true)]
    [InlineData("no-stock",0,3,null,"3",false)]
    public void RegisteredV01FixturesMatchIndependentVerification(string name,int a,int b,string? c,string cost,bool feasible)
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"V01",name+".json"));
        var source=FactoryAdmission.Read(bytes).Dataset;
        var clock=source.Clock;
        var doc=UdirDocument.Create(VerifierTests.Model() with{RevisionId=source.RevisionId,
            InputDigests=[VerifierTests.Hash(bytes)],Scenarios=[],Requires=[],
            TimeBasis=new(clock.CalendarId,clock.TimeZoneId,clock.Origin,clock.TickUnit)});
        var scope=VerifierTests.Scope(doc);
        var starts=ImmutableDictionary<string,string>.Empty.Add("A",a.ToString()).Add("B",b.ToString());
        if(c is not null)starts=starts.Add("C",c);
        var report=PilotVerifier.Check(doc,Catalog(),scope,bytes,new(scope,starts,cost));
        Assert.Equal(feasible,report.Status==VerificationStatus.VerifiedFeasible);
        Assert.Equal(cost,report.ExactCost);
        Assert.Equal(source.ObjectiveUnit,report.ObjectiveUnit);
        if(!feasible)Assert.Contains(report.Diagnostics,x=>x.Code=="pilot.materialPrefix");
        var wrong=scope with{RevisionId="stale"};
        Assert.Equal(VerificationStatus.Rejected,PilotVerifier.Check(doc,Catalog(),wrong,bytes,new(wrong,starts,cost)).Status);
    }
}
