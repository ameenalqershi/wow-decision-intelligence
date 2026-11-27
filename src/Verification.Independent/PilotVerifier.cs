using System.Collections.Immutable;
using WowDi.Manufacturing.Data;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.Verification.Independent;

public sealed record PilotCandidate(VerificationScope Scope,ImmutableDictionary<string,string> Starts,string ReportedCost);
public sealed record PilotCheck(string Code,string? OriginalId=null);
public sealed record PilotContribution(string OperationId,long Start,long End,long Tardiness,string Cost);
public sealed record PilotVerification(VerificationScope Scope,string? SourceSha256,VerificationStatus Status,string? ExactCost,
    string? Currency,string? ObjectiveUnit,ImmutableArray<PilotContribution> Contributions,ImmutableArray<PilotCheck> Diagnostics);

// Original production specification evaluator; no formulation, solver, lowering or reuse of research implementation.
// It certifies only this frozen source profile, not unrelated UDIR rows or additional objectives.
public static class PilotVerifier
{
    private sealed record Occupancy(FactoryOperation Op,long Start,long End);
    private sealed record MaterialEvent(long Tick,bool Receipt,Rational Quantity,string Id);

    public static PilotVerification Check(UdirDocument revision,MeasurementSemantics measurements,VerificationScope scope,ReadOnlyMemory<byte> frozenSource,
        PilotCandidate candidate,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(revision);ArgumentNullException.ThrowIfNull(measurements);ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(candidate);
        var model=revision.Model;
        var diagnostics=ImmutableArray.CreateBuilder<PilotCheck>();
        var contributions=ImmutableArray.CreateBuilder<PilotContribution>();
        string? digest=null,currency=null,cost=null,objectiveUnit=null;
        PilotVerification Report(VerificationStatus status)=>new(scope,digest,status,cost,currency,objectiveUnit,contributions.ToImmutable(),diagnostics.ToImmutable());
        PilotVerification Reject(string code){diagnostics.Add(new(code));return Report(VerificationStatus.Rejected);}
        void Require(bool valid,string code){if(!valid)throw new VerificationFailure(code);}
        try
        {
            ct.ThrowIfCancellationRequested();
            Require(scope==candidate.Scope&&scope.TenantId==model.TenantId&&scope.ModelId==model.ModelId&&scope.RevisionId==model.RevisionId&&
                !string.IsNullOrWhiteSpace(scope.RunId)&&scope.ModelSha256==OriginalVerifier.Hash(revision.Write()),"pilot.scope");
            Require(frozenSource.Length is >0 and <=FactoryAdmission.MaxBytes,"pilot.sourceSize");
            var frozen=FactoryAdmission.Read(frozenSource.Span);
            digest=frozen.Sha256;
            Require(model.InputDigests.Contains(digest,StringComparer.Ordinal),"pilot.sourceDigest");
            var source=frozen.Dataset;
            Require(source.RevisionId==scope.RevisionId&&source.Clock.CalendarId==model.TimeBasis.CalendarId&&
                source.Clock.TimeZoneId==model.TimeBasis.TimeZoneId&&source.Clock.Origin==model.TimeBasis.Origin&&
                source.Clock.TickUnit==model.TimeBasis.TickUnit,"pilot.scope");
            Require(model.Scenarios.IsEmpty&&(source.Currency is null||model.Types.Any(t=>t.Kind==ScalarKind.Money&&t.Currency==source.Currency)),"pilot.profile");
            revision.ValidateMeasurements(measurements);
            currency=source.Currency;objectiveUnit=source.ObjectiveUnit;
            var machines=source.Calendars.ToDictionary(x=>x.MachineId,StringComparer.Ordinal);
            var operations=source.Operations.ToDictionary(x=>x.OperationId,StringComparer.Ordinal);
            var materials=source.Materials.ToDictionary(x=>x.MaterialId,StringComparer.Ordinal);
            var calendars=machines.ToDictionary(x=>x.Key,x=>x.Value.Downtime.OrderBy(d=>d.StartMinute).ToArray(),StringComparer.Ordinal);
            var events=materials.Keys.ToDictionary(x=>x,_=>new List<MaterialEvent>(),StringComparer.Ordinal);
            var stock=new Dictionary<string,Rational>(StringComparer.Ordinal);
            foreach(var material in materials.Values)
            {
                Require(Text(material.Unit),"pilot.unit");
                _=measurements.ConvertUnit("1",material.Unit,material.Unit);
                var initial=Rational.Parse(material.InitialQuantity);Require(initial.N.Sign>=0,"pilot.quantity");
                stock.Add(material.MaterialId,initial);
            }
            foreach(var receipt in source.Receipts)
            {
                Require(receipt is not null&&materials.TryGetValue(receipt.MaterialId,out var m)&&m.Unit==receipt.Unit&&
                    receipt.AvailableMinute>=0&&receipt.AvailableMinute<=source.Clock.HorizonMinutes,"pilot.receipt");
                var quantity=Rational.Parse(receipt!.Quantity);Require(quantity.N.Sign>=0,"pilot.quantity");
                events[receipt.MaterialId].Add(new(receipt.AvailableMinute,true,quantity,receipt.MaterialId));
            }
            Require(candidate.Starts is not null,"pilot.assignments");
            var starts=candidate.Starts!.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            var occupancy=new Dictionary<string,Occupancy>(StringComparer.Ordinal);
            var total=Rational.Zero;
            foreach(var op in operations.Values.OrderBy(o=>o.OperationId,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                Require(Text(op.JobId)&&machines.ContainsKey(op.MachineId)&&op.ReleaseMinute>=0&&op.ReleaseMinute<=source.Clock.HorizonMinutes&&
                    op.DurationMinutes>0&&op.DurationMinutes<=source.Clock.HorizonMinutes&&op.DueMinute>=0&&op.HardDeadlineMinute is not <0&&!op.Materials.IsDefault,"pilot.operation");
                var weight=Rational.Parse(op.TardinessWeight);Require(weight.N.Sign>=0,"pilot.weight");
                var demands=new Dictionary<string,Rational>(StringComparer.Ordinal);
                foreach(var demand in op.Materials)
                {
                    Require(demand is not null&&materials.TryGetValue(demand.MaterialId,out var material)&&material.Unit==demand.Unit,"pilot.unit");
                    var quantity=Rational.Parse(demand!.Quantity);
                    Require(quantity.N.Sign>=0&&demands.TryAdd(demand.MaterialId,quantity),"pilot.demand");
                }
                if(!starts.Remove(op.OperationId,out var raw)){diagnostics.Add(new("pilot.missingStart",op.OperationId));continue;}
                var start=Rational.Parse(raw);
                if(!start.Integral||start.N<op.ReleaseMinute||start.N>source.Clock.HorizonMinutes-op.DurationMinutes)
                {diagnostics.Add(new("pilot.interval",op.OperationId));continue;}
                var s=(long)start.N;var end=s+op.DurationMinutes;
                if(op.HardDeadlineMinute is { } hard&&end>hard)diagnostics.Add(new("pilot.hardDeadline",op.OperationId));
                occupancy.Add(op.OperationId,new(op,s,end));
                foreach(var (material,quantity) in demands)events[material].Add(new(s,false,quantity,op.OperationId));
                var late=end>op.DueMinute?end-op.DueMinute:0;
                var contribution=weight.Multiply(Rational.From(late));
                contributions.Add(new(op.OperationId,s,end,late,contribution.ToString()));
                total=total.Add(contribution);
            }
            if(starts.Count!=0)diagnostics.Add(new("pilot.extraStart"));
            foreach(var p in source.Precedence)
            {
                Require(p is not null&&operations.ContainsKey(p.Predecessor)&&operations.ContainsKey(p.Successor)&&p.Predecessor!=p.Successor,"pilot.precedenceSource");
                if(occupancy.TryGetValue(p!.Predecessor,out var before)&&occupancy.TryGetValue(p.Successor,out var after)&&before.End>after.Start)
                    diagnostics.Add(new("pilot.precedence",p.Successor));
            }
            var byMachine=occupancy.Values.ToLookup(x=>x.Op.MachineId,StringComparer.Ordinal);
            foreach(var machine in machines.Keys)
            {
                ct.ThrowIfCancellationRequested();
                var assigned=byMachine[machine].OrderBy(x=>x.Start).ThenBy(x=>x.Op.OperationId,StringComparer.Ordinal);
                long lastEnd=0;var downtime=calendars[machine];var i=0;
                foreach(var row in assigned)
                {
                    if(row.Start<lastEnd)diagnostics.Add(new("pilot.machineOverlap",row.Op.OperationId));
                    lastEnd=Math.Max(lastEnd,row.End);
                    while(i<downtime.Length&&downtime[i].EndMinute<=row.Start)i++;
                    if(i<downtime.Length&&downtime[i].StartMinute<row.End)diagnostics.Add(new("pilot.downtime",row.Op.OperationId));
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
        catch(DatasetException ex){return Reject("pilot.source."+ex.Diagnostic.Code);}
    }
    private static bool Text(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=256&&value==value.Trim()&&!value.Any(char.IsControl);
}
