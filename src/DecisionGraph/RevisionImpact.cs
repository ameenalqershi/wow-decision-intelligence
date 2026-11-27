using System.Collections.Immutable;

namespace WowDi.DecisionGraph;

public sealed record RevisionImpact(GraphRevision Before, GraphRevision After,
    ImmutableArray<string> ChangedEntities, ImmutableArray<string> ChangedRelationships,
    ImmutableArray<string> AffectedEntities, ImmutableArray<string> AffectedRelationships)
{
    public static RevisionImpact Compare(DecisionGraphSnapshot before, DecisionGraphSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Revision.TenantId != after.Revision.TenantId || before.Revision.ModelId != after.Revision.ModelId)
            throw new GraphValidationException("graph.identity", "Compare revisions of the same tenant and model.");

        var changedEntities = Changed(before.Entities, after.Entities, (left, right) => left == right);
        var changedRelationships = Changed(before.Relationships, after.Relationships, Equivalent);
        if (before.Revision == after.Revision && (changedEntities.Length > 0 || changedRelationships.Length > 0))
            throw new GraphValidationException("graph.revisionConflict", "A frozen revision has inconsistent content.");

        var entityIds = changedEntities.ToHashSet(StringComparer.Ordinal);
        var edgeIds = changedRelationships.ToHashSet(StringComparer.Ordinal);
        var entities = new Queue<string>(changedEntities);
        var edges = new Queue<string>(changedRelationships);
        while (entities.Count > 0 || edges.Count > 0)
        {
            while (entities.TryDequeue(out var entityId))
            {
                VisitIncidence(before, entityId);
                VisitIncidence(after, entityId);
            }
            while (edges.TryDequeue(out var edgeId))
            {
                VisitEndpoints(before, edgeId);
                VisitEndpoints(after, edgeId);
            }
        }
        return new(before.Revision, after.Revision, changedEntities, changedRelationships,
            Ordered(entityIds), Ordered(edgeIds));

        void VisitIncidence(DecisionGraphSnapshot graph, string entityId)
        {
            if (!graph.Entities.ContainsKey(entityId)) return;
            foreach (var id in graph.IncidentRelationships(entityId))
                if (edgeIds.Add(id)) edges.Enqueue(id);
        }

        void VisitEndpoints(DecisionGraphSnapshot graph, string edgeId)
        {
            if (!graph.Relationships.TryGetValue(edgeId, out var edge)) return;
            if (entityIds.Add(edge.OwnerId)) entities.Enqueue(edge.OwnerId);
            foreach (var id in edge.Targets)
                if (entityIds.Add(id)) entities.Enqueue(id);
        }
    }

    private static bool Equivalent(GraphRelationship left, GraphRelationship right) =>
        left.Kind == right.Kind && left.OwnerId == right.OwnerId && left.Source == right.Source &&
        left.Window == right.Window && left.Capacity == right.Capacity &&
        left.Targets.AsSpan().SequenceEqual(right.Targets.AsSpan());

    private static ImmutableArray<string> Changed<T>(IReadOnlyDictionary<string, T> before,
        IReadOnlyDictionary<string, T> after, Func<T, T, bool> equal)
    {
        var ids = before.Keys.Where(id => !after.TryGetValue(id, out var current) || !equal(before[id], current))
            .Concat(after.Keys.Where(id => !before.ContainsKey(id)));
        return Ordered(ids);
    }

    private static ImmutableArray<string> Ordered(IEnumerable<string> ids) =>
        ids.Order(StringComparer.Ordinal).ToImmutableArray();
}
