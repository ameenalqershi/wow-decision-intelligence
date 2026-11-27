using System.Collections.Immutable;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.Verification.Independent;

// Internal frozen pilot source profile, not a public REST/import DTO. V01/M10 own mapping.
public sealed record PilotDowntime(long Start,long End);
public sealed record PilotMachine(string Id,ImmutableArray<PilotDowntime> Downtime);
public sealed record PilotMaterial(string Id,string Unit,string Initial);
public sealed record PilotReceipt(string MaterialId,string Unit,string Quantity,long AvailableAt);
public sealed record PilotDemand(string MaterialId,string Unit,string Quantity);
public sealed record PilotOperation(string Id,string JobId,string MachineId,long Release,long Duration,long Due,
    string Weight,string WeightUnit,ImmutableArray<PilotDemand> Demands,long? HardDue=null);
public sealed record PilotPrecedence(string Before,string After);
public sealed record PilotSource(string TenantId,string ModelId,string RevisionId,string CalendarId,string TimeZoneId,string Origin,
    long Horizon,string Currency,ImmutableArray<PilotMachine> Machines,ImmutableArray<PilotOperation> Operations,
    ImmutableArray<PilotMaterial> Materials,ImmutableArray<PilotReceipt> Receipts,ImmutableArray<PilotPrecedence> Precedence);
public sealed record PilotCandidate(VerificationScope Scope,ImmutableDictionary<string,string> Starts,string ReportedCost);
public sealed record PilotCheck(string Code,string? OriginalId=null);
public sealed record PilotContribution(string OperationId,long Start,long End,long Tardiness,string Cost);
public sealed record PilotVerification(VerificationScope Scope,string? SourceSha256,VerificationStatus Status,string? ExactCost,
    string? Currency,ImmutableArray<PilotContribution> Contributions,ImmutableArray<PilotCheck> Diagnostics);

// Original production specification evaluator; no formulation, solver, lowering or reuse of research implementation.
// It certifies only this frozen source profile, not unrelated UDIR rows or additional objectives.
public static class PilotVerifier
{
    private static readonly JsonSerializerOptions JsonOptions=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,RespectNullableAnnotations=true,
        RespectRequiredConstructorParameters=true,AllowDuplicateProperties=false,
        UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,MaxDepth=16
    };
    private sealed record Occupancy(PilotOperation Op,long Start,long End);
    private sealed record MaterialEvent(long Tick,bool Receipt,Rational Quantity,string Id);

    public static PilotVerification Check(UdirDocument revision,MeasurementSemantics measurements,VerificationScope scope,ReadOnlyMemory<byte> frozenSource,
        PilotCandidate candidate,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(revision);ArgumentNullException.ThrowIfNull(measurements);ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(candidate);
        var model=revision.Model;
        var diagnostics=ImmutableArray.CreateBuilder<PilotCheck>();
        var contributions=ImmutableArray.CreateBuilder<PilotContribution>();
        string? digest=null,currency=null,cost=null;
        PilotVerification Report(VerificationStatus status)=>new(scope,digest,status,cost,currency,contributions.ToImmutable(),diagnostics.ToImmutable());
        PilotVerification Reject(string code){diagnostics.Add(new(code));return Report(VerificationStatus.Rejected);}
        void Require(bool valid,string code){if(!valid)throw new VerificationFailure(code);}
        try
        {
            ct.ThrowIfCancellationRequested();
            Require(scope==candidate.Scope&&scope.TenantId==model.TenantId&&scope.ModelId==model.ModelId&&scope.RevisionId==model.RevisionId&&
                !string.IsNullOrWhiteSpace(scope.RunId)&&scope.ModelSha256==OriginalVerifier.Hash(revision.Write()),"pilot.scope");
            Require(frozenSource.Length is >0 and <=1024*1024,"pilot.sourceSize");
            var bytes=frozenSource.ToArray();
            digest=OriginalVerifier.Hash(bytes);
            Require(model.InputDigests.Contains(digest,StringComparer.Ordinal),"pilot.sourceDigest");
            var source=JsonSerializer.Deserialize<PilotSource>(bytes,JsonOptions)??throw new VerificationFailure("pilot.source");
            Require(source.TenantId==scope.TenantId&&source.ModelId==scope.ModelId&&source.RevisionId==scope.RevisionId&&
                source.CalendarId==model.TimeBasis.CalendarId&&source.TimeZoneId==model.TimeBasis.TimeZoneId&&
                source.Origin==model.TimeBasis.Origin&&model.TimeBasis.TickUnit=="minute","pilot.scope");
            Require(source.Horizon>0&&source.Currency is {Length:3}&&source.Currency.All(char.IsAsciiLetterUpper),"pilot.metadata");
            Require(model.Scenarios.IsEmpty&&model.Types.Any(t=>t.Kind==ScalarKind.Money&&t.Currency==source.Currency),"pilot.profile");
            revision.ValidateMeasurements(measurements);
            currency=source.Currency;
            var machines=Index(source.Machines,x=>x.Id);
            var operations=Index(source.Operations,x=>x.Id);
            var materials=Index(source.Materials,x=>x.Id);
            Require(machines.Count>0&&operations.Count>0&&!source.Receipts.IsDefault&&!source.Precedence.IsDefault,"pilot.source");
            var calendars=new Dictionary<string,PilotDowntime[]>(StringComparer.Ordinal);
            foreach(var machine in machines.Values)
            {
                Require(!machine.Downtime.IsDefault&&machine.Downtime.All(x=>x is not null),"pilot.calendar");
                var sorted=machine.Downtime.OrderBy(x=>x.Start).ThenBy(x=>x.End).ToArray();
                long previousEnd=0;
                foreach(var interval in sorted)
                {
                    Require(interval is not null&&interval.Start>=previousEnd&&interval.End>interval.Start&&interval.End<=source.Horizon,"pilot.calendar");
                    previousEnd=interval!.End;
                }
                calendars.Add(machine.Id,sorted);
            }
            var events=materials.Keys.ToDictionary(x=>x,_=>new List<MaterialEvent>(),StringComparer.Ordinal);
            var stock=new Dictionary<string,Rational>(StringComparer.Ordinal);
            foreach(var material in materials.Values)
            {
                Require(Text(material.Unit),"pilot.unit");
                _=measurements.ConvertUnit("1",material.Unit,material.Unit);
                var initial=Rational.Parse(material.Initial);Require(initial.N.Sign>=0,"pilot.quantity");
                stock.Add(material.Id,initial);
            }
            foreach(var receipt in source.Receipts)
            {
                Require(receipt is not null&&materials.TryGetValue(receipt.MaterialId,out var m)&&m.Unit==receipt.Unit&&
                    receipt.AvailableAt>=0&&receipt.AvailableAt<=source.Horizon,"pilot.receipt");
                var quantity=Rational.Parse(receipt!.Quantity);Require(quantity.N.Sign>=0,"pilot.quantity");
                events[receipt.MaterialId].Add(new(receipt.AvailableAt,true,quantity,receipt.MaterialId));
            }
            Require(candidate.Starts is not null,"pilot.assignments");
            var starts=candidate.Starts!.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            var occupancy=new Dictionary<string,Occupancy>(StringComparer.Ordinal);
            var total=Rational.Zero;
            foreach(var op in operations.Values.OrderBy(o=>o.Id,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                Require(Text(op.JobId)&&machines.ContainsKey(op.MachineId)&&op.Release>=0&&op.Release<=source.Horizon&&
                    op.Duration>0&&op.Duration<=source.Horizon&&op.Due>=0&&op.HardDue is not <0&&!op.Demands.IsDefault&&
                    op.WeightUnit==source.Currency+"/minute","pilot.operation");
                var weight=Rational.Parse(op.Weight);Require(weight.N.Sign>=0,"pilot.weight");
                var demands=new Dictionary<string,Rational>(StringComparer.Ordinal);
                foreach(var demand in op.Demands)
                {
                    Require(demand is not null&&materials.TryGetValue(demand.MaterialId,out var material)&&material.Unit==demand.Unit,"pilot.unit");
                    var quantity=Rational.Parse(demand!.Quantity);
                    Require(quantity.N.Sign>=0&&demands.TryAdd(demand.MaterialId,quantity),"pilot.demand");
                }
                if(!starts.Remove(op.Id,out var raw)){diagnostics.Add(new("pilot.missingStart",op.Id));continue;}
                var start=Rational.Parse(raw);
                if(!start.Integral||start.N<op.Release||start.N>source.Horizon-op.Duration)
                {diagnostics.Add(new("pilot.interval",op.Id));continue;}
                var s=(long)start.N;var end=s+op.Duration;
                if(op.HardDue is { } hard&&end>hard)diagnostics.Add(new("pilot.hardDeadline",op.Id));
                occupancy.Add(op.Id,new(op,s,end));
                foreach(var (material,quantity) in demands)events[material].Add(new(s,false,quantity,op.Id));
                var late=end>op.Due?end-op.Due:0;
                var contribution=weight.Multiply(Rational.From(late));
                contributions.Add(new(op.Id,s,end,late,contribution.ToString()));
                total=total.Add(contribution);
            }
            if(starts.Count!=0)diagnostics.Add(new("pilot.extraStart"));
            foreach(var p in source.Precedence)
            {
                Require(p is not null&&operations.ContainsKey(p.Before)&&operations.ContainsKey(p.After)&&p.Before!=p.After,"pilot.precedenceSource");
                if(occupancy.TryGetValue(p!.Before,out var before)&&occupancy.TryGetValue(p.After,out var after)&&before.End>after.Start)
                    diagnostics.Add(new("pilot.precedence",p.After));
            }
            var byMachine=occupancy.Values.ToLookup(x=>x.Op.MachineId,StringComparer.Ordinal);
            foreach(var machine in machines.Keys)
            {
                ct.ThrowIfCancellationRequested();
                var assigned=byMachine[machine].OrderBy(x=>x.Start).ThenBy(x=>x.Op.Id,StringComparer.Ordinal);
                long lastEnd=0;var downtime=calendars[machine];var i=0;
                foreach(var row in assigned)
                {
                    if(row.Start<lastEnd)diagnostics.Add(new("pilot.machineOverlap",row.Op.Id));
                    lastEnd=Math.Max(lastEnd,row.End);
                    while(i<downtime.Length&&downtime[i].End<=row.Start)i++;
                    if(i<downtime.Length&&downtime[i].Start<row.End)diagnostics.Add(new("pilot.downtime",row.Op.Id));
                }
            }
            foreach(var (id,rows) in events)
            {
                ct.ThrowIfCancellationRequested();
                var available=stock[id];
                // All receipts at a tick precede all starts at that tick. Starts have no order priority.
                foreach(var entry in rows.OrderBy(x=>x.Tick).ThenByDescending(x=>x.Receipt).ThenBy(x=>x.Id,StringComparer.Ordinal))
                {
                    available=entry.Receipt?available.Add(entry.Quantity):available.Subtract(entry.Quantity);
                    if(!entry.Receipt&&available.N.Sign<0)diagnostics.Add(new("pilot.materialPrefix",id));
                }
            }
            if(occupancy.Count==operations.Count)
            {
                cost=total.ToString();
                if(Rational.Parse(candidate.ReportedCost)!=total)diagnostics.Add(new("pilot.objectiveMismatch"));
            }
            return Report(diagnostics.Count==0?VerificationStatus.VerifiedFeasible:VerificationStatus.Rejected);
        }
        catch(VerificationFailure ex){return Reject(ex.Code);}
        catch(ContractException){return Reject("pilot.model");}
        catch(JsonException){return Reject("pilot.sourceFormat");}
    }
    private static bool Text(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=256&&value==value.Trim()&&!value.Any(char.IsControl);
    private static Dictionary<string,T> Index<T>(ImmutableArray<T> rows,Func<T,string> id) where T:class
    {
        if(rows.IsDefault||rows.Length>10000)throw new VerificationFailure("pilot.source");
        var result=new Dictionary<string,T>(StringComparer.Ordinal);
        foreach(var row in rows)
            if(row is null||!Text(id(row))||!result.TryAdd(id(row),row))throw new VerificationFailure("pilot.sourceId");
        return result;
    }
}
