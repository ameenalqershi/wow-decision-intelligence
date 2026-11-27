using System.Collections.Immutable;

namespace WowDi.DecisionGraph;

public enum EntityKind
{
    Entity, Resource, Activity, Decision, Variable, Parameter, DerivedExpression,
    Constraint, Objective, ObjectiveTerm, Event, Policy, Risk, Outcome, Order, Source
}

public enum RelationKind
{
    Requires, Consumes, Produces, Precedes, ConstraintScope, ObjectiveScope, Capacity, Availability, Provenance
}

public sealed record GraphRevision(string TenantId, string ModelId, string RevisionId);
public sealed record SourceStamp(string SourceKey, string RecordKey, string MappingVersion, DateTimeOffset ObservedAt);
public sealed record TimeWindow(DateTimeOffset Start, DateTimeOffset End, string CalendarId);
public sealed record CapacityLimit(decimal Amount, string Unit);
public sealed record GraphEntity(string Id, EntityKind Kind, SourceStamp Source);
public sealed record RelationshipInput(string Id, RelationKind Kind, string OwnerId, IReadOnlyList<string> Targets,
    SourceStamp Source, TimeWindow? Window = null, CapacityLimit? Capacity = null);
public sealed record GraphRelationship(string Id, RelationKind Kind, string OwnerId, ImmutableArray<string> Targets,
    SourceStamp Source, TimeWindow? Window, CapacityLimit? Capacity);
public sealed class GraphValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
