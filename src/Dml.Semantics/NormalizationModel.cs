using System.Collections.Immutable;
using WowDi.Udir;

namespace WowDi.Dml.Semantics;

public sealed record SemanticType(ScalarKind Kind,string? Unit,string? Currency)
{
    internal static SemanticType From(ScalarType type)=>new(type.Kind,type.Unit,type.Currency);
}
public sealed record NormalizedExpression(string Id,Operation Operation,SemanticType Type,
    ImmutableArray<string> Arguments,string? Literal,string? SymbolId);
public sealed record ExpressionOrigin(string OriginalId,SourceSpan Span,string NormalizedId,string Rewrite);
public sealed record NormalizedConstraint(string Id,string PredicateId,bool Hard,string? Penalty,SourceSpan Span);
public sealed record NormalizedObjective(string Id,string ExpressionId,Direction Direction,int Priority,string Weight,string Tolerance,SourceSpan Span);
public sealed record SemanticDiagnostic(string Code,string Message,SourceSpan? Span);

public sealed class NormalizedModel
{
    public UdirDocument Original {get;}
    public ImmutableDictionary<string,NormalizedExpression> Expressions {get;}
    public ImmutableArray<ExpressionOrigin> Origins {get;}
    public ImmutableArray<NormalizedConstraint> Constraints {get;}
    public ImmutableArray<NormalizedObjective> Objectives {get;}
    /// <summary>Mathematical projection only. Not a revision digest or authorization/evidence identity.</summary>
    public ImmutableArray<byte> CanonicalBytes {get;}
    public string SemanticDigest {get;}
    internal NormalizedModel(UdirDocument original,ImmutableDictionary<string,NormalizedExpression> expressions,
        ImmutableArray<ExpressionOrigin> origins,ImmutableArray<NormalizedConstraint> constraints,
        ImmutableArray<NormalizedObjective> objectives,ImmutableArray<byte> bytes,string digest)
        =>(Original,Expressions,Origins,Constraints,Objectives,CanonicalBytes,SemanticDigest)=(original,expressions,origins,constraints,objectives,bytes,digest);
}
public sealed record NormalizationResult(NormalizedModel? Model,SemanticDiagnostic? Diagnostic)
{
    public bool Accepted=>Model is not null&&Diagnostic is null;
}
