using System.Collections.Immutable;
using WowDi.DecisionGraph;

namespace WowDi.Studio.Visualizations;

public sealed record GraphNodeView(string Id, EntityKind Kind, int Y);
public sealed record GraphEdgeView(string Id, RelationKind Kind, string OwnerId, ImmutableArray<string> Targets, int Y);
public sealed record GraphIncidenceView(string EdgeId, string EntityId, bool IsOwner, int NodeY, int EdgeY);
public sealed record GraphProjection(GraphRevision Revision, ImmutableArray<GraphNodeView> Nodes,
    ImmutableArray<GraphEdgeView> Edges, ImmutableArray<GraphIncidenceView> Incidence, int Height)
{
    public static GraphProjection Create(DecisionGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var nodes = snapshot.Entities.Values.Select((n,i) => new GraphNodeView(n.Id,n.Kind,40+i*48)).ToImmutableArray();
        var edges = snapshot.Relationships.Values.Select((e,i) => new GraphEdgeView(e.Id,e.Kind,e.OwnerId,e.Targets,40+i*48)).ToImmutableArray();
        var positions = nodes.ToDictionary(n=>n.Id,n=>n.Y,StringComparer.Ordinal);
        var incidence = edges.SelectMany(e=>new[]{new GraphIncidenceView(e.Id,e.OwnerId,true,positions[e.OwnerId],e.Y)}
            .Concat(e.Targets.Select(t=>new GraphIncidenceView(e.Id,t,false,positions[t],e.Y)))).ToImmutableArray();
        return new(snapshot.Revision,nodes,edges,incidence,Math.Max(100,48*Math.Max(nodes.Length,edges.Length)+40));
    }
}
