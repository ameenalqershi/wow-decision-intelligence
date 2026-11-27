using WowDi.DecisionGraph;
using Xunit;

namespace WowDi.DecisionGraph.Tests;

public sealed class GraphTests
{
    private static readonly GraphRevision Revision = new("tenant", "factory", "r1");
    private static readonly SourceStamp Source = new("fixture", "record", "v1", DateTimeOffset.UnixEpoch);
    private static readonly GraphEntity[] Nodes = [new("a", EntityKind.Variable, Source), new("b", EntityKind.Variable, Source),
        new("c", EntityKind.Variable, Source), new("cap", EntityKind.Constraint, Source),
        new("cost", EntityKind.Objective, Source), new("machine", EntityKind.Resource, Source), new("shift", EntityKind.Activity, Source)];
    private static RelationshipInput Scope(params string[] targets) =>
        new("scope", RelationKind.ConstraintScope, "cap", targets, Source);
    private static DecisionGraphSnapshot Graph(params RelationshipInput[] edges) => DecisionGraphSnapshot.Create(Revision, Nodes, edges);

    [Fact]
    public void CompleteHyperedgeIsImmutableAndIndexedInBothDirections()
    {
        string[] vars = ["c","a","b"];
        var graph = Graph(Scope(vars), new("objective", RelationKind.ObjectiveScope, "cost", ["b"], Source));
        vars[0] = "deleted";
        Assert.Equal(new[] { "a", "b", "c" }, graph.Relationships["scope"].Targets.ToArray());
        Assert.Equal(new[] { "objective", "scope" }, graph.IncidentRelationships("b").ToArray());
        Assert.Equal("scope", Assert.Single(graph.IncidentRelationships("cap")));
        Assert.Equal(Source, graph.Relationships["scope"].Source);
        Assert.Empty(graph.IncidentRelationships("machine"));
    }

    [Fact]
    public void DistinctRevisionsAndInputOrderPreserveSnapshotIdentity()
    {
        var original = Graph(Scope("a", "b"));
        var later = DecisionGraphSnapshot.Create(Revision with { RevisionId = "r2" }, Nodes.Reverse(), [Scope("c")]);
        Assert.Equal(original.Entities.Keys, later.Entities.Keys);
        Assert.NotEqual(original.Revision, later.Revision);
        Assert.Equal(new[] { "a", "b" }, original.Relationships["scope"].Targets.ToArray());
        Assert.Empty(later.IncidentRelationships("a"));
    }

    [Fact]
    public void AvailabilityAndZeroCapacityRetainUnitsAndInstants()
    {
        var window = new TimeWindow(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), "UTC/shift");
        var graph = Graph(new("hours", RelationKind.Availability, "machine", ["shift"], Source, Window:window),
            new("limit", RelationKind.Capacity, "machine", ["shift"], Source, Capacity:new(0, "machine-minute")));
        Assert.Equal(window, graph.Relationships["hours"].Window);
        Assert.Equal(new CapacityLimit(0, "machine-minute"), graph.Relationships["limit"].Capacity);
    }

    [Fact]
    public void AllDomainConceptsAndConstantConstraintsAreRepresentable()
    {
        var entities = Enum.GetValues<EntityKind>().Select(k => new GraphEntity(k.ToString(), k, Source));
        var graph = DecisionGraphSnapshot.Create(Revision, entities,
            [new("constant", RelationKind.ConstraintScope, "Constraint", [], Source)]);
        Assert.Equal(Enum.GetValues<EntityKind>().Length, graph.Entities.Count);
        Assert.Empty(graph.Relationships["constant"].Targets);
    }

    [Theory]
    [InlineData("dangling", "graph.reference")] [InlineData("owner", "graph.reference")]
    [InlineData("duplicateTarget", "graph.incidence")] [InlineData("wrongScope", "graph.scope")]
    [InlineData("emptyRelation", "graph.incidence")] [InlineData("missingMapping", "graph.provenance")]
    [InlineData("selfPrecedence", "graph.precedence")] [InlineData("unexpectedWindow", "graph.window")]
    [InlineData("reversedWindow", "graph.window")] [InlineData("negativeCapacity", "graph.capacity")]
    [InlineData("invalidKind", "graph.relationship")]
    public void RejectsInvalidRelationsAtomically(string fixture, string code)
    {
        var zero = DateTimeOffset.UnixEpoch;
        var edge = fixture switch
        {
            "dangling" => Scope("missing"), "owner" => Scope("a") with { OwnerId = "missing" },
            "duplicateTarget" => Scope("a", "a"), "wrongScope" => Scope("a") with { OwnerId = "machine" },
            "emptyRelation" => Scope() with { Kind = RelationKind.Requires },
            "missingMapping" => Scope() with { Source = Source with { MappingVersion = "" } },
            "selfPrecedence" => new("p", RelationKind.Precedes, "shift", ["shift"], Source),
            "unexpectedWindow" => Scope() with { Window = new(zero, zero.AddDays(1), "cal") },
            "reversedWindow" => new("t", RelationKind.Availability, "machine", ["shift"], Source, new(zero.AddDays(1),zero,"cal")),
            "negativeCapacity" => new("k", RelationKind.Capacity, "machine", ["shift"], Source, Capacity:new(-1,"units")),
            _ => Scope() with { Kind = (RelationKind)999 }
        };
        Assert.Equal(code, Assert.Throws<GraphValidationException>(() => Graph(edge)).Code);
    }

    [Fact]
    public void RejectsDuplicateIdsAndMissingRevision()
    {
        Assert.Equal("graph.duplicate", Assert.Throws<GraphValidationException>(() =>
            DecisionGraphSnapshot.Create(Revision, Nodes.Append(Nodes[0]), [])).Code);
        Assert.Equal("graph.duplicate", Assert.Throws<GraphValidationException>(() => Graph(Scope(), Scope())).Code);
        Assert.Equal("graph.revision", Assert.Throws<GraphValidationException>(() =>
            DecisionGraphSnapshot.Create(Revision with { TenantId = " " }, Nodes, [])).Code);
        Assert.Equal("graph.reference", Assert.Throws<GraphValidationException>(() => Graph().IncidentRelationships("foreign")).Code);
    }
}
