using System.Collections.Immutable;

namespace WowDi.DecisionGraph;

public sealed class DecisionGraphSnapshot
{
    private readonly ImmutableDictionary<string, ImmutableArray<string>> incidence;
    public GraphRevision Revision { get; }
    public ImmutableSortedDictionary<string, GraphEntity> Entities { get; }
    public ImmutableSortedDictionary<string, GraphRelationship> Relationships { get; }

    private DecisionGraphSnapshot(GraphRevision revision, ImmutableSortedDictionary<string, GraphEntity> nodes,
        ImmutableSortedDictionary<string, GraphRelationship> edges, Dictionary<string, List<string>> links)
    {
        Revision = revision;
        Entities = nodes;
        Relationships = edges;
        incidence = links.ToImmutableDictionary(pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToImmutableArray(), StringComparer.Ordinal);
    }

    public ImmutableArray<string> IncidentRelationships(string entityId) =>
        incidence.TryGetValue(entityId, out var result) ? result : throw Invalid("graph.reference", "Unknown entity ID.");

    public static DecisionGraphSnapshot Create(GraphRevision revision, IEnumerable<GraphEntity> entities,
        IEnumerable<RelationshipInput> relationships)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationships);
        if (Blank(revision.TenantId) || Blank(revision.ModelId) || Blank(revision.RevisionId))
            throw Invalid("graph.revision", "Tenant, model and revision identity are required.");
        var nodes = ImmutableSortedDictionary.CreateBuilder<string, GraphEntity>(StringComparer.Ordinal);
        var links = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            if (entity is null || Blank(entity.Id) || !Enum.IsDefined(entity.Kind))
                throw Invalid("graph.entity", "Invalid entity ID or kind.");
            CheckSource(entity.Source);
            if (!links.TryAdd(entity.Id, [])) throw Invalid("graph.duplicate", "Repeated entity ID.");
            nodes.Add(entity.Id, entity);
        }

        var edges = ImmutableSortedDictionary.CreateBuilder<string, GraphRelationship>(StringComparer.Ordinal);
        foreach (var input in relationships)
        {
            if (input is null || Blank(input.Id) || !Enum.IsDefined(input.Kind) || input.Targets is null)
                throw Invalid("graph.relationship", "Invalid relationship identity, kind or incidence list.");
            if (edges.ContainsKey(input.Id)) throw Invalid("graph.duplicate", "Repeated relationship ID.");
            CheckSource(input.Source);
            if (Blank(input.OwnerId) || !nodes.TryGetValue(input.OwnerId, out var owner))
                throw Invalid("graph.reference", "The relationship owner is absent from this revision.");
            var targets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var target in input.Targets)
            {
                if (Blank(target) || !nodes.ContainsKey(target))
                    throw Invalid("graph.reference", "A relationship target is absent from this revision.");
                if (!targets.Add(target)) throw Invalid("graph.incidence", "Repeated target in a hyperedge.");
            }
            CheckSemantics(input, owner, targets);
            edges.Add(input.Id, new GraphRelationship(input.Id, input.Kind, owner.Id, [.. targets],
                input.Source, input.Window, input.Capacity));
            links[owner.Id].Add(input.Id);
            foreach (var target in targets)
                if (target != owner.Id) links[target].Add(input.Id);
        }
        return new DecisionGraphSnapshot(revision, nodes.ToImmutable(), edges.ToImmutable(), links);
    }

    private static void CheckSemantics(RelationshipInput input, GraphEntity owner, SortedSet<string> targets)
    {
        if (input.Kind == RelationKind.ConstraintScope && owner.Kind != EntityKind.Constraint ||
            input.Kind == RelationKind.ObjectiveScope && owner.Kind is not (EntityKind.Objective or EntityKind.ObjectiveTerm))
            throw Invalid("graph.scope", "Scope owner is incompatible with the relation.");
        if (targets.Count == 0 && input.Kind is not (RelationKind.ConstraintScope or RelationKind.ObjectiveScope))
            throw Invalid("graph.incidence", "A non-scope relationship needs at least one target.");
        if (input.Kind == RelationKind.Precedes && targets.Contains(owner.Id))
            throw Invalid("graph.precedence", "An activity cannot precede itself.");
        if (input.Kind == RelationKind.Capacity)
        {
            if (owner.Kind != EntityKind.Resource || input.Capacity is not { Amount: >= 0 } cap || Blank(cap.Unit))
                throw Invalid("graph.capacity", "A resource capacity needs a nonnegative amount and unit.");
        }
        else if (input.Capacity is not null) throw Invalid("graph.capacity", "Unexpected capacity metadata.");
        if (input.Kind == RelationKind.Availability)
        {
            if (input.Window is not { } w || w.End <= w.Start || Blank(w.CalendarId))
                throw Invalid("graph.window", "Availability needs a nonempty half-open interval and a calendar.");
        }
        else if (input.Window is not null) throw Invalid("graph.window", "Unexpected availability metadata.");
    }

    private static void CheckSource(SourceStamp source)
    {
        if (source is null || Blank(source.SourceKey) || Blank(source.RecordKey) || Blank(source.MappingVersion))
            throw Invalid("graph.provenance", "Source key, record identity and mapping version are required.");
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
    private static GraphValidationException Invalid(string code, string message) => new(code, message);
}
