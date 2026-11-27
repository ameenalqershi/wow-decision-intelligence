using System.Collections.Immutable;

namespace WowDi.Udir;

public enum ScalarKind { Boolean, Integer, Rational, Instant, Duration, Quantity, Money, Rate }
public enum SymbolRole { Variable, Parameter, Resource }
public enum Operation { Literal, Symbol, Negate, Add, Subtract, Multiply, Divide, Equal, LessOrEqual, GreaterOrEqual }
public enum Direction { Minimize, Maximize }

public sealed record SourceDocument(string Id, string Sha256, int Utf16Length);
public sealed record SourceSpan(string SourceId, int Start, int Length);
public sealed record ScalarType(string Id, ScalarKind Kind, string? Unit = null, string? Currency = null);
public sealed record TimeBasis(string CalendarId, string TimeZoneId, string Origin, string TickUnit);
public sealed record Symbol(string Id, SymbolRole Role, string TypeId, SourceSpan Span, string? Lower = null, string? Upper = null);
public sealed record Expression(string Id, Operation Op, string TypeId, ImmutableArray<string> Arguments, SourceSpan Span,
    string? Literal = null, string? SymbolId = null);
public sealed record Constraint(string Id, string PredicateId, bool Hard, SourceSpan Span, string? Penalty = null);
public sealed record Objective(string Id, string ExpressionId, Direction Direction, int Priority, string Weight, string Tolerance, SourceSpan Span);
public sealed record ScenarioValue(string SymbolId, string Value);
public sealed record Scenario(string Id, int Stage, string InformationSet, SourceSpan Span, ImmutableArray<ScenarioValue> Overrides, string? ParentId = null, string? Probability = null);
public sealed record Model(string TenantId, string ModelId, string RevisionId, string SemanticsVersion,
    ImmutableArray<string> InputDigests, ImmutableArray<SourceDocument> Sources, TimeBasis TimeBasis,
    ImmutableArray<ScalarType> Types, ImmutableArray<Symbol> Symbols, ImmutableArray<Expression> Expressions,
    ImmutableArray<Constraint> Constraints, ImmutableArray<Objective> Objectives, ImmutableArray<Scenario> Scenarios,
    ImmutableArray<string> Requires);
