using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Solver.Contracts;
using WowDi.Udir;

namespace WowDi.Verification.Independent;

public enum VerificationStatus { VerifiedFeasible,Rejected,Unsupported,NoCandidate }
public sealed record VerificationScope(string TenantId,string ModelId,string RevisionId,string RunId,string ModelSha256);
public sealed record VerificationDiagnostic(string Code,string? OriginalId=null,SourceSpan? Span=null);
public sealed record CheckedConstraint(string Id,bool Hard,bool Satisfied,string? ViolatedPenalty,SourceSpan Span);
public sealed record CheckedObjective(string Id,Direction Direction,int Priority,string ExactValue,string? Unit,string? Currency,string Weight,string Tolerance,bool ReportMatches,SourceSpan Span);
public sealed record VerificationReport(VerificationScope Scope,VerificationStatus Status,ImmutableArray<CheckedConstraint> Constraints,
    ImmutableArray<CheckedObjective> Objectives,ImmutableArray<VerificationDiagnostic> Diagnostics,string? FactsSha256,string? WitnessSha256)
{
    // Only primal feasibility and original objective observations. Never a proof of optimum/infeasibility.
    public bool Accepted=>Status==VerificationStatus.VerifiedFeasible;
}

public static class OriginalVerifier
{
    private readonly record struct Value(bool? Bool,Rational Number)
    {
        internal static Value Numeric(Rational value)=>new(null,value);
        internal static Value Boolean(bool value)=>new(value,Rational.Zero);
        internal static Value Parse(string? raw,ScalarKind type)
        {
            if(type==ScalarKind.Boolean)return raw switch{"true"=>Boolean(true),"false"=>Boolean(false),_=>throw new VerificationFailure("verify.boolean")};
            var number=Rational.Parse(raw);
            if(type is ScalarKind.Integer or ScalarKind.Instant&&!number.Integral)throw new VerificationFailure("verify.integrality");
            return Numeric(number);
        }
    }

    // Scope/digest are trusted from the frozen registry; factsBytes is the exact registered
    // flat JSON string-valued binding snapshot for non-variable symbols. It must be a frozen input digest.
    public static VerificationReport Check(UdirDocument document,MeasurementSemantics measurements,
        VerificationScope scope,SolverResult candidate,ReadOnlyMemory<byte>? factsBytes=null,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(document);ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(scope);ArgumentNullException.ThrowIfNull(candidate);
        var model=document.Model;
        var checks=ImmutableArray.CreateBuilder<CheckedConstraint>();
        var objectives=ImmutableArray.CreateBuilder<CheckedObjective>();
        var errors=ImmutableArray.CreateBuilder<VerificationDiagnostic>();
        string? factsDigest=null,witnessDigest=null;
        VerificationReport Report(VerificationStatus status)=>new(scope,status,checks.ToImmutable(),objectives.ToImmutable(),errors.ToImmutable(),factsDigest,witnessDigest);
        VerificationReport Reject(string code,VerificationStatus status=VerificationStatus.Rejected)
        {errors.Add(new(code));return Report(status);}
        ct.ThrowIfCancellationRequested();
        try
        {
            if(scope.TenantId!=model.TenantId||scope.ModelId!=model.ModelId||scope.RevisionId!=model.RevisionId||
                string.IsNullOrWhiteSpace(scope.RunId)||scope.ModelSha256!=Hash(document.Write())||
                candidate.TenantId!=scope.TenantId||candidate.ModelId!=scope.ModelId||candidate.RevisionId!=scope.RevisionId||
                candidate.RunId!=scope.RunId)return Reject("verify.scope");
            if(!Enum.IsDefined(candidate.MathematicalStatus)||!Enum.IsDefined(candidate.Termination))return Reject("verify.outcome");
            if(candidate.Incumbent is null)return Report(VerificationStatus.NoCandidate);
            if(candidate.MathematicalStatus==MathematicalStatus.Infeasible)return Reject("verify.inconsistentStatus");
            if(!model.Scenarios.IsEmpty||model.Requires.Any(x=>x!="integer.linear"))
                return Reject("verify.unsupportedProfile",VerificationStatus.Unsupported);
            document.ValidateMeasurements(measurements);
            var types=model.Types.ToDictionary(x=>x.Id,StringComparer.Ordinal);
            var variables=candidate.Incumbent.ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            var facts=ReadFacts(model,factsBytes,out factsDigest);
            var values=new Dictionary<string,Value>(StringComparer.Ordinal);
            foreach(var symbol in model.Symbols)
            {
                ct.ThrowIfCancellationRequested();
                var type=types[symbol.TypeId].Kind;
                try
                {
                    string? raw;
                    if(symbol.Role==SymbolRole.Variable)
                    {
                        if(!variables.Remove(symbol.Id,out raw))throw new VerificationFailure("verify.missingAssignment");
                    }
                    else if(!facts.Remove(symbol.Id,out raw))
                    {
                        if(symbol.Lower is null||symbol.Lower!=symbol.Upper)
                            throw new VerificationFailure("verify.unfrozenFact");
                        raw=symbol.Lower;
                    }
                    var value=Value.Parse(raw,type);
                    if(value.Bool is null&&((symbol.Lower is not null&&value.Number.CompareTo(Rational.Parse(symbol.Lower))<0)||
                        (symbol.Upper is not null&&value.Number.CompareTo(Rational.Parse(symbol.Upper))>0)))
                        throw new VerificationFailure("verify.domain");
                    values.Add(symbol.Id,value);
                }
                catch(VerificationFailure ex){errors.Add(new(ex.Code,symbol.Id,symbol.Span));}
            }
            if(variables.Count!=0||facts.Count!=0)errors.Add(new("verify.extraBinding"));
            if(errors.Count!=0)return Report(VerificationStatus.Rejected);
            witnessDigest=Hash(JsonSerializer.SerializeToUtf8Bytes(new {scope,factsDigest,assignments=candidate.Incumbent.OrderBy(x=>x.Key,StringComparer.Ordinal)}));
            var nodes=model.Expressions.ToDictionary(e=>e.Id,StringComparer.Ordinal);
            // Iterative topological evaluation; no recursive stack growth on large DAGs.
            var indegrees=nodes.Values.ToDictionary(e=>e.Id,e=>e.Arguments.Length,StringComparer.Ordinal);
            var users=nodes.Keys.ToDictionary(x=>x,_=>new List<string>(),StringComparer.Ordinal);
            foreach(var node in nodes.Values)foreach(var arg in node.Arguments)users[arg].Add(node.Id);
            var queue=new Queue<string>(model.Expressions.Where(e=>e.Arguments.IsEmpty).Select(e=>e.Id));
            var results=new Dictionary<string,Value>(StringComparer.Ordinal);
            while(queue.TryDequeue(out var id))
            {
                ct.ThrowIfCancellationRequested();
                var e=nodes[id];var type=types[e.TypeId].Kind;
                try
                {
                    var a=e.Arguments.Length>0?results[e.Arguments[0]]:default;
                    var b=e.Arguments.Length>1?results[e.Arguments[1]]:default;
                    var value=e.Op switch
                    {
                        Operation.Literal=>Value.Parse(e.Literal,type),
                        Operation.Symbol=>values[e.SymbolId!],
                        Operation.Negate=>Value.Numeric(a.Number.Negate()),
                        Operation.Add=>Value.Numeric(a.Number.Add(b.Number)),
                        Operation.Subtract=>Value.Numeric(a.Number.Subtract(b.Number)),
                        Operation.Multiply=>Value.Numeric(a.Number.Multiply(b.Number)),
                        Operation.Divide=>Value.Numeric(a.Number.Divide(b.Number)),
                        Operation.Equal=>Value.Boolean(a.Bool is not null?a.Bool==b.Bool:a.Number.CompareTo(b.Number)==0),
                        Operation.LessOrEqual=>Value.Boolean(a.Number.CompareTo(b.Number)<=0),
                        Operation.GreaterOrEqual=>Value.Boolean(a.Number.CompareTo(b.Number)>=0),
                        _=>throw new VerificationFailure("verify.operator")
                    };
                    if(type is ScalarKind.Integer or ScalarKind.Instant&&!value.Number.Integral)
                        throw new VerificationFailure("verify.integrality");
                    results.Add(id,value);
                }
                catch(VerificationFailure ex){errors.Add(new(ex.Code,e.Id,e.Span));return Report(VerificationStatus.Rejected);}
                foreach(var parent in users[id])if(--indegrees[parent]==0)queue.Enqueue(parent);
            }
            if(results.Count!=nodes.Count)return Reject("verify.dag");
            foreach(var row in model.Constraints)
            {
                ct.ThrowIfCancellationRequested();
                var satisfied=results[row.PredicateId].Bool==true;
                checks.Add(new(row.Id,row.Hard,satisfied,!row.Hard&&!satisfied?row.Penalty:null,row.Span));
                if(row.Hard&&!satisfied)errors.Add(new("verify.hardConstraint",row.Id,row.Span));
            }
            var reported=new Dictionary<string,ObjectiveObservation>(StringComparer.Ordinal);
            if(candidate.Objectives.IsDefault)return Reject("verify.objectives");
            foreach(var observation in candidate.Objectives)
            {
                if(observation is null||string.IsNullOrWhiteSpace(observation.ObjectiveId)||!reported.TryAdd(observation.ObjectiveId,observation))
                    return Reject("verify.objectives");
            }
            foreach(var objective in model.Objectives.OrderBy(x=>x.Priority))
            {
                ct.ThrowIfCancellationRequested();
                var actual=results[objective.ExpressionId].Number;
                var type=types[nodes[objective.ExpressionId].TypeId];
                var matches=false;
                if(reported.Remove(objective.Id,out var observed))
                {
                    try
                    {
                        matches=observed.IncumbentValue is not null&&Rational.Parse(observed.IncumbentValue)==actual&&
                            Rational.Parse(observed.AbsoluteTolerance).N.Sign>=0&&Rational.Parse(observed.RelativeTolerance).N.Sign>=0;
                    }
                    catch(VerificationFailure){}
                }
                objectives.Add(new(objective.Id,objective.Direction,objective.Priority,actual.ToString(),type.Unit,type.Currency,
                    objective.Weight,objective.Tolerance,matches,objective.Span));
                if(!matches)errors.Add(new("verify.objectiveMismatch",objective.Id,objective.Span));
            }
            if(reported.Count!=0)errors.Add(new("verify.extraObjective"));
            return Report(errors.Count==0?VerificationStatus.VerifiedFeasible:VerificationStatus.Rejected);
        }
        catch(VerificationFailure ex){return Reject(ex.Code);}
        catch(ContractException){return Reject("verify.measurementOrModel");}
        catch(JsonException){return Reject("verify.factsFormat");}
    }

    private static Dictionary<string,string> ReadFacts(Model model,ReadOnlyMemory<byte>? supplied,out string? digest)
    {
        digest=null;
        var facts=new Dictionary<string,string>(StringComparer.Ordinal);
        if(supplied is not { } memory)return facts;
        if(memory.Length is <1 or >1024*1024)throw new VerificationFailure("verify.factsSize");
        var bytes=memory.ToArray();
        digest=Hash(bytes);
        if(!model.InputDigests.Contains(digest,StringComparer.Ordinal))throw new VerificationFailure("verify.factsDigest");
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{AllowDuplicateProperties=false,MaxDepth=4});
        if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new VerificationFailure("verify.factsFormat");
        foreach(var item in doc.RootElement.EnumerateObject())
        {
            if(item.Value.ValueKind!=JsonValueKind.String||!facts.TryAdd(item.Name,item.Value.GetString()!))
                throw new VerificationFailure("verify.factsFormat");
        }
        return facts;
    }
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
