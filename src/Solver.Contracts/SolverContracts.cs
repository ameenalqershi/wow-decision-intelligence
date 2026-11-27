using System.Collections.Immutable;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.Solver.Contracts;

public sealed record SolverIdentity(string SolverId, string Version, string RuntimeId)
{
    internal void Check() => Guard.Require(Guard.Text(SolverId) && Guard.Text(Version) && Guard.Text(RuntimeId), "identity");
}

public sealed record SolverCapabilities(SolverIdentity Solver, ImmutableArray<string> Features,
    ImmutableArray<ScalarKind> Domains, string? MaxCoefficientMagnitude, long? MaxHorizonTicks, int MaxThreads,
    bool SupportsHints, bool SupportsInterruption, bool SupportsBounds)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Solver);
        Solver.Check();
        Guard.Require(!Features.IsDefault && Features.All(Guard.Text) && Features.Distinct(StringComparer.Ordinal).Count() == Features.Length, "features");
        Guard.Require(!Domains.IsDefaultOrEmpty && Domains.All(d => Enum.IsDefined(d)) && Domains.Distinct().Count() == Domains.Length, "domains");
        Guard.Require(MaxThreads > 0 && MaxHorizonTicks is not < 0 &&
            (MaxCoefficientMagnitude is null || ExactValues.Compare(MaxCoefficientMagnitude, "0") >= 0), "limits");
    }

    public ImmutableArray<string> MissingFeatures(UdirDocument document)
    {
        Validate();
        return document.Model.Requires.Except(Features, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }
}

public enum MathematicalStatus { Optimal, Feasible, Infeasible, Unbounded, Unknown }
public enum Termination { Completed, Timeout, Cancelled, Failed }
public enum EvidenceSource { SolverAttestation, IndependentCertificate }
public sealed record Evidence(EvidenceSource Source, string Reference)
{
    internal void Check() => Guard.Require(Enum.IsDefined(Source) && Guard.Text(Reference), "evidence");
}
public sealed record Measurement(long? ElapsedMs, long? CpuMs, long? PeakBytes);
public sealed record ObjectiveBound(string Value, Direction Direction, Evidence Evidence);
public sealed record ObjectiveObservation(string ObjectiveId, string? IncumbentValue, ObjectiveBound? Bound,
    string AbsoluteTolerance, string RelativeTolerance);
public sealed record SolverResult(string TenantId, string ModelId, string RevisionId, string RunId,
    SolverIdentity Solver, MathematicalStatus MathematicalStatus, Termination Termination,
    ImmutableDictionary<string, string>? Incumbent, ImmutableArray<ObjectiveObservation> Objectives,
    Evidence? StatusEvidence, Measurement Resources)
{
    public void ValidateAgainst(UdirDocument document, SolverCapabilities manifest)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        var model = document.Model;
        Guard.Require(TenantId == model.TenantId && ModelId == model.ModelId && RevisionId == model.RevisionId &&
            Guard.Text(RunId) && Solver == manifest.Solver, "scope");
        Guard.Require(Enum.IsDefined(MathematicalStatus) && Enum.IsDefined(Termination), "outcome");
        Guard.Require(Resources is { ElapsedMs: not < 0, CpuMs: not < 0, PeakBytes: not < 0 }, "measurement");
        StatusEvidence?.Check();
        if (MathematicalStatus is MathematicalStatus.Optimal or MathematicalStatus.Infeasible or MathematicalStatus.Unbounded)
            Guard.Require(StatusEvidence != null, "proof");
        if (MathematicalStatus is MathematicalStatus.Feasible or MathematicalStatus.Optimal)
            Guard.Require(Incumbent != null, "incumbent");
        if (MathematicalStatus == MathematicalStatus.Infeasible)
            Guard.Require(Incumbent == null, "incumbent");
        if (Incumbent != null) CheckAssignment(model, Incumbent);

        Guard.Require(!Objectives.IsDefault && Objectives.Length == model.Objectives.Length, "objectives");
        var remaining = model.Objectives.ToDictionary(o => o.Id, StringComparer.Ordinal);
        foreach (var row in Objectives)
        {
            if (row is null || !Guard.Text(row.ObjectiveId) || !remaining.Remove(row.ObjectiveId, out var objective))
                throw Guard.Error("objectives");
            Guard.Require(ExactValues.Compare(row.AbsoluteTolerance, "0") >= 0 && ExactValues.Compare(row.RelativeTolerance, "0") >= 0, "tolerance");
            Guard.Require((row.IncumbentValue != null) == (Incumbent != null), "objectiveValue");
            if (row.IncumbentValue != null) _ = ExactValues.Compare(row.IncumbentValue, "0");
            if (row.Bound is not { } bound) continue;
            Guard.Require(manifest.SupportsBounds && bound.Direction == objective.Direction && bound.Evidence != null, "bound");
            bound.Evidence!.Check();
            _ = ExactValues.Compare(bound.Value, "0");
            if (row.IncumbentValue != null)
            {
                int order = ExactValues.Compare(bound.Value, row.IncumbentValue);
                Guard.Require(objective.Direction == Direction.Minimize ? order <= 0 : order >= 0, "boundOrder");
            }
        }
    }

    private static void CheckAssignment(Model model, ImmutableDictionary<string, string> supplied)
    {
        // Snapshot with ordinal IDs even when an adapter used a case-insensitive dictionary.
        var values = supplied.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var types = model.Types.ToDictionary(t => t.Id, t => t.Kind, StringComparer.Ordinal);
        foreach (var variable in model.Symbols.Where(s => s.Role == SymbolRole.Variable))
        {
            if (!values.Remove(variable.Id, out var raw)) throw Guard.Error("assignment");
            var kind = types[variable.TypeId];
            if (kind == ScalarKind.Boolean) Guard.Require(raw is "true" or "false", "assignment");
            else
            {
                _ = ExactValues.Compare(raw, "0");
                if (kind is ScalarKind.Integer or ScalarKind.Instant) Guard.Require(!raw.Contains('/'), "assignment");
            }
        }
        Guard.Require(values.Count == 0, "assignment");
    }
}

internal static class Guard
{
    internal static bool Text(string? s) => !string.IsNullOrWhiteSpace(s);
    internal static ContractException Error(string field) => new("solver." + field, "Invalid solver contract: " + field + ".");
    internal static void Require(bool value, string field)
    {
        if (!value) throw Error(field);
    }
}
