using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WowDi.Manufacturing.Data;
using Xunit;

public sealed class FactoryTests
{
    static byte[] Bytes(string name="baseline")=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures",name+".json"));
    static FrozenFactoryDataset Read(string name="baseline")=>FactoryAdmission.Read(Bytes(name));
    static void Reject(string expected,Action<JsonObject> change)
    {
        var json=JsonNode.Parse(Bytes())!.AsObject();change(json);
        var error=Assert.Throws<DatasetException>(()=>FactoryAdmission.Read(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal(expected,error.Diagnostic.Code);Assert.StartsWith("$",error.Diagnostic.Path);
    }
    [Fact] public void FreezesExactBytesAndHash()
    {
        var bytes=Bytes();var original=(byte[])bytes.Clone();var f=FactoryAdmission.Read(bytes);
        bytes[0]=0;
        Assert.Equal(original,f.SourceBytes.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)),f.Sha256);
        Assert.NotEqual(f.Sha256,Read("outage").Sha256);
        Assert.Equal("synthetic-base-v1",f.Dataset.RevisionId);
    }
    [Theory]
    [InlineData("baseline")][InlineData("outage")][InlineData("multi")][InlineData("no-stock")]
    public void AdmitsStructurallyValidSourceEvenIfInfeasible(string name)=>Assert.NotEmpty(Read(name).Dataset.Operations);
    [Fact] public void RejectsMissingRequiredMember()=>Reject("json",j=>j.Remove("receipts"));
    [Fact] public void RejectsUnknownMember()=>Reject("json",j=>j["unknown"]=1);
    [Fact] public void RejectsNullArray()=>Reject("json",j=>j["operations"]=null);
    [Fact] public void RejectsNullRow()=>Reject("row",j=>j["operations"]![0]=null);
    [Fact] public void RejectsDuplicateJsonMember()
    {
        var bytes=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bytes()).Replace("\"revisionId\":","\"revisionId\":\"other\",\"revisionId\":"));
        Assert.Equal("duplicate-member",Assert.Throws<DatasetException>(()=>FactoryAdmission.Read(bytes)).Diagnostic.Code);
    }
    [Fact] public void RejectsUnknownProfile()=>Reject("profile",j=>j["profile"]="unknown");
    [Fact] public void RejectsMissingMachineCalendar()=>Reject("missing-calendar",j=>j["operations"]![0]!["machineId"]="missing");
    [Fact] public void RejectsDuplicateMachine()=>Reject("duplicate-machine",j=>j["calendars"]!.AsArray().Add(j["calendars"]![0]!.DeepClone()));
    [Fact] public void RejectsOverlapWithSourceRow()=>Reject("calendar-overlap",j=>j["calendars"]![0]!["downtime"]=JsonNode.Parse("[{\"startMinute\":1,\"endMinute\":4},{\"startMinute\":3,\"endMinute\":5}]"));
    [Fact] public void TouchingCalendarsAreValid()
    {
        var j=JsonNode.Parse(Bytes())!;j["calendars"]![0]!["downtime"]=JsonNode.Parse("[{\"startMinute\":1,\"endMinute\":4},{\"startMinute\":4,\"endMinute\":5}]");
        Assert.Equal(2,FactoryAdmission.Read(Encoding.UTF8.GetBytes(j.ToJsonString())).Dataset.Calendars[0].Downtime.Length);
    }
    [Fact] public void RejectsEmptyDowntime()=>Reject("interval",j=>j["calendars"]![0]!["downtime"]=JsonNode.Parse("[{\"startMinute\":4,\"endMinute\":4}]"));
    [Fact] public void RejectsMissingMaterial()=>Reject("material-reference",j=>j["operations"]![0]!["materials"]![0]!["materialId"]="missing");
    [Fact] public void RejectsUnitMismatch()=>Reject("unit-mismatch",j=>j["receipts"]![0]!["unit"]="kg");
    [Fact] public void RejectsNegativeWeight()=>Reject("negative",j=>j["operations"]![0]!["tardinessWeight"]="-1");
    [Theory][InlineData("1.5")][InlineData("2/4")][InlineData("01")][InlineData("1/0")]
    public void RejectsNoncanonicalExactQuantity(string value)=>Reject("exact-number",j=>j["receipts"]![0]!["quantity"]=value);
    [Fact] public void RejectsWrongTickUnit()=>Reject("tick-unit",j=>j["clock"]!["tickUnit"]="second");
    [Fact] public void RejectsInvalidOrigin()=>Reject("origin",j=>j["clock"]!["origin"]="2026-01-01");
    [Fact] public void RejectsInvalidTimezone()=>Reject("timezone",j=>j["clock"]!["timeZoneId"]="not/a/zone");
    [Fact] public void RejectsNegativeRelease()=>Reject("release",j=>j["operations"]![0]!["releaseMinute"]=-1);
    [Fact] public void RejectsZeroDuration()=>Reject("duration",j=>j["operations"]![0]!["durationMinutes"]=0);
    [Fact] public void RejectsNegativeDue()=>Reject("due",j=>j["operations"]![0]!["dueMinute"]=-1);
    [Fact] public void RejectsReceiptPastHorizon()=>Reject("receipt-time",j=>j["receipts"]![0]!["availableMinute"]=11);
    [Fact] public void RejectsCurrencyMismatch()=>Reject("currency",j=>j["currency"]="USD");
    [Fact] public void RejectsCycle()=>Reject("precedence-cycle",j=>j["precedence"]=JsonNode.Parse("[{\"predecessor\":\"A\",\"successor\":\"B\"},{\"predecessor\":\"B\",\"successor\":\"A\"}]"));
    [Fact] public void RejectsDanglingPredecessor()=>Reject("precedence-reference",j=>j["precedence"]=JsonNode.Parse("[{\"predecessor\":\"X\",\"successor\":\"A\"}]"));
    [Fact] public void RejectsDuplicateOperation()=>Reject("duplicate-operation",j=>j["operations"]!.AsArray().Add(j["operations"]![0]!.DeepClone()));
    [Fact] public void DueBeyondHorizonAndZeroWeightAreAllowed()
    {
        var j=JsonNode.Parse(Bytes())!;j["operations"]![0]!["dueMinute"]=100;j["operations"]![0]!["tardinessWeight"]="0";
        var f=FactoryAdmission.Read(Encoding.UTF8.GetBytes(j.ToJsonString()));
        Assert.Equal(100,f.Dataset.Operations[0].DueMinute);Assert.Equal("0",f.Dataset.Operations[0].TardinessWeight);
    }

    // Deliberately independent, tiny, exhaustive tick oracle. Test-only;
    // never used by an adapter, runtime or dispatch implementation.
    private readonly record struct Q(BigInteger N,BigInteger D):IComparable<Q>
    {
        public static Q Parse(string s){var p=s.Split('/');return new(BigInteger.Parse(p[0],CultureInfo.InvariantCulture),p.Length==1?BigInteger.One:BigInteger.Parse(p[1],CultureInfo.InvariantCulture));}
        public static Q operator +(Q a,Q b)=>new(a.N*b.D+b.N*a.D,a.D*b.D);
        public static Q operator *(Q a,int n)=>new(a.N*n,a.D);
        public int CompareTo(Q b)=>(N*b.D).CompareTo(b.N*D);
    }
    private static (Q? Cost,int[]? Starts,int Feasible,int Evaluated) Enumerate(FactoryDataset f)
    {
        Assert.InRange(f.Operations.Length,1,4);Assert.InRange(f.Clock.HorizonMinutes,1,20);
        int[] starts=new int[f.Operations.Length];Q? best=null;int[]? bestStarts=null;int feasible=0,evaluated=0;
        var ops=f.Operations;var index=ops.Select((o,i)=>(o.OperationId,i)).ToDictionary(x=>x.OperationId,x=>x.i);
        bool Valid()
        {
            for(int i=0;i<ops.Length;i++)
                if(starts[i]<ops[i].ReleaseMinute||starts[i]+ops[i].DurationMinutes>f.Clock.HorizonMinutes||
                   ops[i].HardDeadlineMinute is int deadline&&starts[i]+ops[i].DurationMinutes>deadline)return false;
            foreach(var edge in f.Precedence)
                if(starts[index[edge.Predecessor]]+ops[index[edge.Predecessor]].DurationMinutes>starts[index[edge.Successor]])return false;
            for(int tick=0;tick<=f.Clock.HorizonMinutes;tick++)
            {
                foreach(var cal in f.Calendars)
                {
                    int occupied=ops.Select((o,i)=>(o,i)).Count(x=>x.o.MachineId==cal.MachineId&&starts[x.i]<=tick&&tick<starts[x.i]+x.o.DurationMinutes);
                    if(occupied>1||occupied>0&&cal.Downtime.Any(d=>d.StartMinute<=tick&&tick<d.EndMinute))return false;
                }
                foreach(var mat in f.Materials)
                {
                    Q available=Q.Parse(mat.InitialQuantity),used=Q.Parse("0");
                    foreach(var r in f.Receipts.Where(r=>r.MaterialId==mat.MaterialId&&r.AvailableMinute<=tick))available+=Q.Parse(r.Quantity);
                    for(int i=0;i<ops.Length;i++)if(starts[i]<=tick)
                        foreach(var c in ops[i].Materials.Where(c=>c.MaterialId==mat.MaterialId))used+=Q.Parse(c.Quantity);
                    if(used.CompareTo(available)>0)return false;
                }
            }
            return true;
        }
        void Visit(int i)
        {
            if(i<starts.Length){for(int t=0;t<=f.Clock.HorizonMinutes;t++){starts[i]=t;Visit(i+1);}return;}
            evaluated++;if(!Valid())return;feasible++;
            Q cost=Q.Parse("0");for(int j=0;j<ops.Length;j++)cost+=Q.Parse(ops[j].TardinessWeight)*Math.Max(0,starts[j]+ops[j].DurationMinutes-ops[j].DueMinute);
            if(best is null||cost.CompareTo(best.Value)<0){best=cost;bestStarts=(int[])starts.Clone();}
        }
        Visit(0);return(best,bestStarts,feasible,evaluated);
    }
    [Theory]
    [InlineData("baseline","3",0,3)]
    [InlineData("outage","12",3,6)]
    public void IndependentlyEnumeratesHandCheckedOptima(string name,string cost,int a,int b)
    {
        var f=Read(name);var result=Enumerate(f.Dataset);
        Assert.NotNull(result.Cost);Assert.Equal(0,result.Cost.Value.CompareTo(Q.Parse(cost)));
        Assert.Equal(new[]{a,b},result.Starts);Assert.Equal(121,result.Evaluated);Assert.True(result.Feasible>0);
        Console.WriteLine($"{name} SHA256={f.Sha256} evaluated={result.Evaluated} feasible={result.Feasible} optimum={cost} starts={a},{b}");
    }
    [Fact] public void MultiMachineRationalStockAndPrecedenceHaveKnownOptimum()
    {
        var f=Read("multi");var result=Enumerate(f.Dataset);
        Assert.NotNull(result.Cost);Assert.Equal(0,result.Cost.Value.CompareTo(Q.Parse("1/2")));
        Assert.Equal(new[]{0,4,6},result.Starts);Assert.Equal(2197,result.Evaluated);
        Console.WriteLine($"multi SHA256={f.Sha256} evaluated={result.Evaluated} feasible={result.Feasible} optimum=1/2 USD starts=0,4,6");
    }
    [Fact] public void NoStockIsStructurallyValidButExhaustivelyInfeasible()
    {
        var f=Read("no-stock");var result=Enumerate(f.Dataset);
        Assert.Null(result.Cost);Assert.Equal(0,result.Feasible);Assert.Equal(121,result.Evaluated);
        Console.WriteLine($"no-stock SHA256={f.Sha256} evaluated={result.Evaluated} feasible=0");
    }
}
