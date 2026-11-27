using System.Collections.Immutable;
using WowDi.Dml.Lowering;
using WowDi.Dml.Semantics;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Contracts;

namespace WowDi.Solver.Planning;

public enum SolverFamily { CpSat, Highs }
public enum NumericRequirement { ExactInput, IntegerArithmetic }
/// <summary>Trusted deployment registration, not an untrusted model field or a qualification certificate.
/// R07 and adapter owners must establish the actual artifact/evidence identity and limits before registration.</summary>
public sealed record AdapterProfile(SolverCapabilities Manifest,SolverFamily Family,NumericEncoding Encoding,
    string QualificationSha256,int MinimumThreads,long MinimumMemoryBytes,TimeSpan MinimumDeadline);
public sealed record PlanningRequest(string RuntimeId,WorkerBudget Budget,NumericRequirement NumericRequirement,
    bool RequireInterruption=true,bool RequireBounds=false);
public sealed record ModelFeatures(int Variables,int IntegralVariables,int Constraints,int Objectives,int ScenarioNodes);
public sealed record CandidateAssessment(SolverIdentity Solver,bool Eligible,string Code,SourceSpan? Span);
public sealed record PlannedExecution(AdapterProfile Profile,WorkerBudget Budget,LoweredModel Lowered);
public sealed record PlanningResult(ModelFeatures Features,PlannedExecution? Selection,ImmutableArray<CandidateAssessment> Assessments)
{
    public bool Accepted=>Selection is not null;
}

/// <summary>Deterministic initial policy: exact semantic admission before selection.
/// No native call, performance prediction, or certificate claim.</summary>
public static class SolverPlanner
{
    public static PlanningResult Plan(NormalizedModel model,PlanningRequest request,
        IEnumerable<AdapterProfile> trustedProfiles,ReadOnlyMemory<byte>? frozenFacts=null,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trustedProfiles);ArgumentNullException.ThrowIfNull(request.Budget);
        ct.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(request.RuntimeId)||request.Budget.Threads<1||request.Budget.MemoryBytes<1||
            request.Budget.Deadline<=TimeSpan.Zero||!Enum.IsDefined(request.NumericRequirement))
            throw new ContractException("plan.request","Invalid planning request.");
        // Bound the trusted catalog and snapshot it so enumeration and ordering are deterministic.
        var profiles=trustedProfiles.Take(65).ToArray();
        if(profiles.Length>64)throw new ContractException("plan.catalog","Adapter catalog exceeds the supported limit.");
        var identities=new HashSet<SolverIdentity>();
        foreach(var p in profiles)
        {
            if(p is null||p.Manifest is null||!identities.Add(p.Manifest.Solver))
                throw new ContractException("plan.catalog","Invalid or duplicate adapter identity.");
            p.Manifest.Validate();
            if(!Enum.IsDefined(p.Family)||!Enum.IsDefined(p.Encoding)||
                p.QualificationSha256 is not {Length:64}||!p.QualificationSha256.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f')||
                p.MinimumThreads<1||p.MinimumThreads>p.Manifest.MaxThreads||p.MinimumMemoryBytes<1||p.MinimumDeadline<=TimeSpan.Zero||
                p.Family==SolverFamily.CpSat&&p.Encoding!=NumericEncoding.Int64||
                p.Family==SolverFamily.Highs&&p.Encoding!=NumericEncoding.Binary64ExactInput)
                throw new ContractException("plan.catalog","Invalid deployment profile.");
        }
        var original=model.Original.Model;
        var kinds=original.Types.ToDictionary(t=>t.Id,t=>t.Kind,StringComparer.Ordinal);
        var vars=original.Symbols.Where(s=>s.Role==SymbolRole.Variable).ToArray();
        int integral=vars.Count(v=>kinds[v.TypeId] is ScalarKind.Integer or ScalarKind.Boolean or ScalarKind.Instant);
        var features=new ModelFeatures(vars.Length,integral,model.Constraints.Length,model.Objectives.Length,original.Scenarios.Length);
        var assessments=ImmutableArray.CreateBuilder<CandidateAssessment>();
        var eligible=new List<PlannedExecution>();
        // Copy once; each lowerer validates the actual frozen digest independently.
        if(frozenFacts is {Length:>1048576})throw new ContractException("plan.factsSize","Frozen facts exceed the supported limit.");
        ReadOnlyMemory<byte>? snapshot=frozenFacts.HasValue?new ReadOnlyMemory<byte>(frozenFacts.Value.ToArray()):null;
        foreach(var p in profiles.OrderBy(p=>p.Manifest.Solver.SolverId,StringComparer.Ordinal)
            .ThenBy(p=>p.Manifest.Solver.Version,StringComparer.Ordinal).ThenBy(p=>p.Manifest.Solver.RuntimeId,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string? code=null;SourceSpan? span=null;
            if(p.Manifest.Solver.RuntimeId!=request.RuntimeId)code="plan.runtime";
            else if(p.Manifest.MissingFeatures(model.Original).Length!=0)code="plan.capability";
            else if(request.NumericRequirement==NumericRequirement.IntegerArithmetic&&p.Encoding!=NumericEncoding.Int64)code="plan.numeric";
            else if(request.RequireInterruption&&!p.Manifest.SupportsInterruption)code="plan.interruption";
            else if(request.RequireBounds&&!p.Manifest.SupportsBounds)code="plan.bounds";
            else if(p.MinimumThreads>request.Budget.Threads||p.MinimumMemoryBytes>request.Budget.MemoryBytes||
                    p.MinimumDeadline>request.Budget.Deadline)code="plan.budget";
            else
            {
                var lowered=ExactLowerer.Lower(model,p.Manifest,p.Encoding,snapshot,ct);
                if(!lowered.Accepted){code=lowered.Diagnostic!.Code;span=lowered.Diagnostic.Span;}
                else
                {
                    var budget=request.Budget with{Threads=Math.Min(request.Budget.Threads,p.Manifest.MaxThreads)};
                    eligible.Add(new(p,budget,lowered.Model!));
                }
            }
            assessments.Add(new(p.Manifest.Solver,code is null,code??"plan.eligible",span));
        }
        // Initial deterministic preference, not a measured ranking: all-integral affine -> CP-SAT,
        // any continuous variable -> HiGHS. Equal family preference uses ordinal identity, not catalog order.
        var preferred=features.IntegralVariables==features.Variables?SolverFamily.CpSat:SolverFamily.Highs;
        var selected=eligible.OrderBy(e=>e.Profile.Family==preferred?0:1)
            .ThenBy(e=>e.Profile.Manifest.Solver.SolverId,StringComparer.Ordinal)
            .ThenBy(e=>e.Profile.Manifest.Solver.Version,StringComparer.Ordinal)
            .ThenBy(e=>e.Profile.Manifest.Solver.RuntimeId,StringComparer.Ordinal).FirstOrDefault();
        return new(features,selected,assessments.ToImmutable());
    }
}
