using System.Collections.Immutable;
using System.Security.Cryptography;
using WowDi.Dml.Semantics;
using WowDi.Udir;
using WowDi.Contracts;

namespace WowDi.Dml.Presolve;

public enum RewriteKind { TrueHardRow, DuplicateHardRow, UnusedVariable }
public sealed record RewriteEntry(RewriteKind Kind,string OriginalId,SourceSpan Span,string? RetainedId,string? ReconstructionValue);
public sealed record Contradiction(string ConstraintId,SourceSpan Span,string PredicateId);

/// <summary>A projection of the original model, never a new revision or a solver certificate.</summary>
public sealed class PresolvedModel
{
    public NormalizedModel Original {get;}
    public string OriginalDigest {get;}
    public ImmutableArray<Symbol> Variables {get;}
    public ImmutableArray<NormalizedConstraint> Constraints {get;}
    public ImmutableArray<RewriteEntry> Ledger {get;}
    public ImmutableArray<Contradiction> Contradictions {get;}
    public bool HasConstantContradiction=>!Contradictions.IsEmpty;

    internal PresolvedModel(NormalizedModel original,ImmutableArray<Symbol> variables,
        ImmutableArray<NormalizedConstraint> constraints,ImmutableArray<RewriteEntry> ledger,
        ImmutableArray<Contradiction> contradictions)
    {
        Original=original;
        OriginalDigest=Convert.ToHexStringLower(SHA256.HashData(original.Original.Write()));
        Variables=variables;Constraints=constraints;Ledger=ledger;Contradictions=contradictions;
    }

    /// <summary>Restore eliminated variables only; independent admission/verification still checks the witness.</summary>
    public ImmutableDictionary<string,string> Restore(IReadOnlyDictionary<string,string> projected)
    {
        ArgumentNullException.ThrowIfNull(projected);
        var exact=projected.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        if(exact.Count!=Variables.Length||Variables.Any(s=>!exact.ContainsKey(s.Id)))
            throw new ContractException("presolve.assignment","Projected assignment has incorrect variable identity.");
        foreach(var entry in Ledger)
            if(entry.Kind==RewriteKind.UnusedVariable)
                exact.Add(entry.OriginalId,entry.ReconstructionValue!);
        return exact.ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>Project an original assignment by identity. No feasibility claim.</summary>
    public ImmutableDictionary<string,string> Project(IReadOnlyDictionary<string,string> original)
    {
        ArgumentNullException.ThrowIfNull(original);
        var exact=original.ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        var variables=Original.Original.Model.Symbols.Where(s=>s.Role==SymbolRole.Variable).ToArray();
        if(exact.Count!=variables.Length||variables.Any(s=>!exact.ContainsKey(s.Id)))
            throw new ContractException("presolve.assignment","Original assignment has incorrect variable identity.");
        return Variables.ToImmutableDictionary(s=>s.Id,s=>exact[s.Id],StringComparer.Ordinal);
    }
}

public static class SafePresolve
{
    /// <summary>Only exact, locally justified transformations. All unrecognized features remain unchanged.</summary>
    public static PresolvedModel Apply(NormalizedModel model,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ct.ThrowIfCancellationRequested();
        var source=model.Original.Model;
        var ledger=ImmutableArray.CreateBuilder<RewriteEntry>();
        var contradictions=ImmutableArray.CreateBuilder<Contradiction>();
        var rows=ImmutableArray.CreateBuilder<NormalizedConstraint>();
        var vars=ImmutableArray.CreateBuilder<Symbol>();
        // Do not reason about scenario-specific assignments or unknown mandatory features.
        bool supported=source.Scenarios.IsEmpty&&source.Requires.All(f=>f is "integer.linear" or "rational.linear");
        var seen=new Dictionary<string,NormalizedConstraint>(StringComparer.Ordinal);
        foreach(var row in model.Constraints)
        {
            ct.ThrowIfCancellationRequested();
            if(!supported||!row.Hard){rows.Add(row);continue;}
            var predicate=model.Expressions[row.PredicateId];
            if(predicate.Operation==Operation.Literal&&predicate.Type.Kind==ScalarKind.Boolean)
            {
                if(predicate.Literal=="true")
                {
                    ledger.Add(new(RewriteKind.TrueHardRow,row.Id,row.Span,null,null));
                    continue;
                }
                // Preserve contradiction even if duplicates exist; this is a local counterexample, not a certificate.
                if(predicate.Literal=="false")
                {
                    contradictions.Add(new(row.Id,row.Span,row.PredicateId));rows.Add(row);continue;
                }
            }
            if(seen.TryGetValue(row.PredicateId,out var prior))
            {
                ledger.Add(new(RewriteKind.DuplicateHardRow,row.Id,row.Span,prior.Id,null));
                continue;
            }
            seen.Add(row.PredicateId,row);rows.Add(row);
        }

        // Conservative: a variable appearing anywhere in the original DAG remains, even in unreachable expressions.
        // This avoids removing dependencies needed for independent original-expression evaluation.
        var used=source.Expressions.Where(e=>e.Op==Operation.Symbol).Select(e=>e.SymbolId!)
            .ToHashSet(StringComparer.Ordinal);
        var types=source.Types.ToDictionary(t=>t.Id,StringComparer.Ordinal);
        foreach(var variable in source.Symbols.Where(s=>s.Role==SymbolRole.Variable).OrderBy(s=>s.Id,StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if(!supported||used.Contains(variable.Id)){vars.Add(variable);continue;}
            // UDIR admission already guarantees ordered exact bounds and integral integer/instant endpoints.
            // Only use explicitly admitted endpoint or zero for a fully unbounded scalar; no invented temporal convention.
            var type=types[variable.TypeId];
            if(type.Kind is ScalarKind.Instant or ScalarKind.Duration){vars.Add(variable);continue;}
            var value=type.Kind==ScalarKind.Boolean?"false":variable.Lower??variable.Upper??"0";
            ledger.Add(new(RewriteKind.UnusedVariable,variable.Id,variable.Span,null,value));
        }
        return new(model,vars.ToImmutable(),rows.ToImmutable(),ledger.ToImmutable(),contradictions.ToImmutable());
    }
}
