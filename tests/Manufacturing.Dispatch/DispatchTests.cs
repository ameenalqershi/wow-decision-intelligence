using System.Numerics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WowDi.Manufacturing.Data;
using WowDi.Manufacturing.Dispatch;
using Xunit;

public sealed class DispatchTests
{
    static byte[] Bytes(string name="baseline")=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures",name+".json"));
    static FrozenFactoryDataset Read(string name="baseline")=>FactoryAdmission.Read(Bytes(name));
    static FrozenFactoryDataset Change(string name,Action<JsonObject> action)
    {
        var j=JsonNode.Parse(Bytes(name))!.AsObject();action(j);
        return FactoryAdmission.Read(Encoding.UTF8.GetBytes(j.ToJsonString()));
    }
    // Independent tick-by-tick candidate check, test-only. It shares no
    // timeline algorithm with dispatch and asserts exact stock and objective.
    private readonly record struct Q(BigInteger N,BigInteger D):IComparable<Q>
    {
        public static Q Parse(string s){var p=s.Split('/');return new(BigInteger.Parse(p[0],CultureInfo.InvariantCulture),p.Length==1?BigInteger.One:BigInteger.Parse(p[1],CultureInfo.InvariantCulture));}
        public static Q operator +(Q a,Q b)=>new(a.N*b.D+b.N*a.D,a.D*b.D);
        public static Q operator *(Q a,int n)=>new(a.N*n,a.D);
        public int CompareTo(Q b)=>(N*b.D).CompareTo(b.N*D);
    }
    static void Check(FrozenFactoryDataset source,DispatchResult r)
    {
        var f=source.Dataset;Assert.Equal(DispatchOutcome.Candidate,r.Outcome);
        Assert.Equal(f.RevisionId,r.RevisionId);Assert.Equal(source.Sha256,r.SourceDigest);Assert.Equal(f.ObjectiveUnit,r.ObjectiveUnit);
        Assert.Null(r.Failure);Assert.Equal(f.Operations.Length,r.Assignments.Length);
        var a=r.Assignments.ToDictionary(x=>x.OperationId,StringComparer.Ordinal);Q cost=Q.Parse("0");
        foreach(var op in f.Operations)
        {
            var x=a[op.OperationId];Assert.Equal(op.MachineId,x.MachineId);
            Assert.True(x.StartMinute>=op.ReleaseMinute);Assert.Equal(op.DurationMinutes,x.EndMinute-x.StartMinute);
            Assert.True(x.EndMinute<=f.Clock.HorizonMinutes);
            if(op.HardDeadlineMinute is int deadline)Assert.True(x.EndMinute<=deadline);
            cost+=Q.Parse(op.TardinessWeight)*Math.Max(0,x.EndMinute-op.DueMinute);
        }
        foreach(var edge in f.Precedence)Assert.True(a[edge.Predecessor].EndMinute<=a[edge.Successor].StartMinute);
        for(int t=0;t<=f.Clock.HorizonMinutes;t++)
        {
            foreach(var cal in f.Calendars)
            {
                int n=r.Assignments.Count(x=>x.MachineId==cal.MachineId&&x.StartMinute<=t&&t<x.EndMinute);
                Assert.True(n<=1);
                if(cal.Downtime.Any(d=>d.StartMinute<=t&&t<d.EndMinute))Assert.Equal(0,n);
            }
            foreach(var mat in f.Materials)
            {
                Q available=Q.Parse(mat.InitialQuantity),used=Q.Parse("0");
                foreach(var receipt in f.Receipts.Where(x=>x.MaterialId==mat.MaterialId&&x.AvailableMinute<=t))available+=Q.Parse(receipt.Quantity);
                foreach(var op in f.Operations.Where(x=>a[x.OperationId].StartMinute<=t))
                    foreach(var c in op.Materials.Where(x=>x.MaterialId==mat.MaterialId))used+=Q.Parse(c.Quantity);
                Assert.True(used.CompareTo(available)<=0);
            }
        }
        Assert.NotNull(r.ObjectiveValue);Assert.Equal(0,cost.CompareTo(Q.Parse(r.ObjectiveValue!)));
    }
    [Theory][InlineData("baseline","6",3,0)][InlineData("outage","15",5,3)]
    public void ReferenceEddIsNotClaimedOptimal(string name,string cost,int a,int b)
    {
        var source=Read(name);var r=EarliestDueDate.Schedule(source);Check(source,r);
        Assert.Equal(new[]{"B","A"},r.DispatchOrder);Assert.Equal(cost,r.ObjectiveValue);
        Assert.Equal(a,r.Assignments[0].StartMinute);Assert.Equal(b,r.Assignments[1].StartMinute);
        Console.WriteLine($"{name} digest={source.Sha256} EDD={cost} starts={a},{b}");
    }
    [Fact]public void MultiMachineFractionalAndPrecedence()
    {
        var source=Read("multi");var r=EarliestDueDate.Schedule(source);Check(source,r);
        Assert.Equal("1/2",r.ObjectiveValue);Assert.Equal(new[]{"A","B","C"},r.DispatchOrder);
        Assert.Equal(new[]{0,4,6},r.Assignments.Select(x=>x.StartMinute));
    }
    [Fact]public void ExplicitNoStockFailureDoesNotPublishPartialCandidate()
    {
        var r=EarliestDueDate.Schedule(Read("no-stock"));
        Assert.Equal(DispatchOutcome.NoCandidate,r.Outcome);Assert.Empty(r.Assignments);Assert.Null(r.ObjectiveValue);
        Assert.Equal(DispatchFailureKind.MaterialAvailability,r.Failure!.Kind);Assert.Equal("raw",r.Failure.MaterialId);
        Assert.Equal("A",r.Failure.OperationId);Assert.Equal(1,r.Failure.PlacedCount);
    }
    [Fact]public void CalendarOrDeadlineFailureIsNotGlobalInfeasibility()
    {
        var s=Change("baseline",j=>j["operations"]![0]!["hardDeadlineMinute"]=3);
        var r=EarliestDueDate.Schedule(s);Assert.Equal(DispatchOutcome.NoCandidate,r.Outcome);
        Assert.Equal(DispatchFailureKind.CalendarOrHorizon,r.Failure!.Kind);Assert.Equal("A",r.Failure.OperationId);
        Assert.Null(r.ObjectiveValue);Assert.Empty(r.Assignments);
        // A=0,B=3 is feasible, but EDD committed B first. No proof of
        // infeasibility may be inferred from this baseline failure.
        Check(s,new(EarliestDueDate.Version,s.Dataset.RevisionId,s.Sha256,s.Dataset.ObjectiveUnit,DispatchOutcome.Candidate,
            [new("A","M1",0,3),new("B","M1",3,5)],"3",["A","B"],null));
    }
    [Fact]public void FutureReservedConsumptionPreventsEarlierInsertFromStealingStock()
    {
        var s=Change("baseline",j=>{
            j["calendars"]!.AsArray().Add(JsonNode.Parse("{\"machineId\":\"M2\",\"downtime\":[]}"));
            j["operations"]![1]!["machineId"]="M2";j["operations"]![1]!["releaseMinute"]=3;
            j["receipts"]![0]!["availableMinute"]=6;
        });
        var r=EarliestDueDate.Schedule(s);Check(s,r);
        Assert.Equal(new[]{"B","A"},r.DispatchOrder);
        Assert.Equal(3,r.Assignments[1].StartMinute);Assert.Equal(6,r.Assignments[0].StartMinute);
    }
    [Fact]public void OrdinalTieAndSourceRowPermutationDoNotChangeSchedule()
    {
        var s=Change("baseline",j=>j["operations"]![1]!["dueMinute"]=4);
        var p=Change("baseline",j=>{
            j["operations"]![1]!["dueMinute"]=4;
            var a=j["operations"]!.AsArray();var first=a[0]!.DeepClone();a.RemoveAt(0);a.Add(first);
        });
        var a=EarliestDueDate.Schedule(s);var b=EarliestDueDate.Schedule(p);Check(s,a);Check(p,b);
        Assert.Equal(new[]{"A","B"},a.DispatchOrder);Assert.Equal(a.DispatchOrder.ToArray(),b.DispatchOrder.ToArray());
        Assert.Equal(a.Assignments.ToArray(),b.Assignments.ToArray());Assert.Equal(a.ObjectiveValue,b.ObjectiveValue);
        Assert.NotEqual(a.SourceDigest,b.SourceDigest);
    }
    [Fact]public void ReceiptsAtStartAndSimultaneousConsumptionAreExact()
    {
        var s=Change("baseline",j=>{
            j["calendars"]!.AsArray().Add(JsonNode.Parse("{\"machineId\":\"M2\",\"downtime\":[]}"));
            j["operations"]![1]!["machineId"]="M2";
            j["materials"]![0]!["initialQuantity"]="1/3";j["receipts"]![0]!["quantity"]="2/3";j["receipts"]![0]!["availableMinute"]=0;
            j["operations"]![0]!["materials"]![0]!["quantity"]="1/3";
            j["operations"]![1]!["materials"]![0]!["quantity"]="2/3";
        });
        var r=EarliestDueDate.Schedule(s);Check(s,r);Assert.All(r.Assignments,a=>Assert.Equal(0,a.StartMinute));
    }
    [Fact]public void TouchingCalendarAndLargeHorizonUseEventJumps()
    {
        var s=Change("baseline",j=>{
            j["clock"]!["horizonMinutes"]=int.MaxValue;
            j["calendars"]![0]!["downtime"]=JsonNode.Parse("[{\"startMinute\":0,\"endMinute\":2147483643}]");
        });
        var r=EarliestDueDate.Schedule(s);
        // Five total minutes cannot fit the four-minute tail. Arithmetic
        // uses widened bounds and never loops over billions of ticks.
        Assert.Equal(DispatchOutcome.NoCandidate,r.Outcome);Assert.Equal(DispatchFailureKind.CalendarOrHorizon,r.Failure!.Kind);
    }
    [Fact]public void CancellationDoesNotPublishAResult()
    {
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>EarliestDueDate.Schedule(Read(),cts.Token));
    }
    [Fact]public void RepeatabilityIncludesExactObjectiveAndAssignments()
    {
        var s=Read("multi");var first=EarliestDueDate.Schedule(s);
        for(int i=0;i<20;i++){var r=EarliestDueDate.Schedule(s);Assert.Equal(first.Assignments.ToArray(),r.Assignments.ToArray());Assert.Equal(first.DispatchOrder.ToArray(),r.DispatchOrder.ToArray());Assert.Equal(first.ObjectiveValue,r.ObjectiveValue);}
    }
}
