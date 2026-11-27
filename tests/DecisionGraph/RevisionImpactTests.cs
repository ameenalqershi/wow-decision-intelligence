using WowDi.DecisionGraph;
using Xunit;

namespace WowDi.DecisionGraph.Tests;

public sealed class RevisionImpactTests
{
    private static readonly SourceStamp Source = new("fixture", "row", "map1", DateTimeOffset.UnixEpoch);
    private static readonly GraphRevision Base = new("t", "m", "r1");
    private static readonly GraphEntity[] Entities = [new("x", EntityKind.Variable, Source),new("y", EntityKind.Variable, Source),
        new("z", EntityKind.Variable, Source),new("a", EntityKind.Constraint, Source),new("b", EntityKind.Constraint, Source),
        new("cost", EntityKind.Objective, Source),new("outside", EntityKind.Parameter, Source)];
    private static RelationshipInput Scope(string owner, params string[] vars) =>
        new("edge-" + owner, owner == "cost" ? RelationKind.ObjectiveScope : RelationKind.ConstraintScope, owner, vars, Source);
    private static DecisionGraphSnapshot Build(string revision, params RelationshipInput[] edges) =>
        DecisionGraphSnapshot.Create(Base with { RevisionId = revision }, Entities, edges);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddedOrRemovedGlobalObjectiveJoinsBothBlocks(bool add)
    {
        var a = Scope("a", "x", "y"); var b = Scope("b", "z");
        var disconnected = Build("d", a, b);
        var connected = Build("c", a, b, Scope("cost", "x", "z"));
        var impact = add ? RevisionImpact.Compare(disconnected, connected) : RevisionImpact.Compare(connected, disconnected);
        Assert.Empty(impact.ChangedEntities);
        Assert.Equal("edge-cost", Assert.Single(impact.ChangedRelationships));
        Assert.Equal(new[] { "a", "b", "cost", "x", "y", "z" }, impact.AffectedEntities.ToArray());
        Assert.Equal(new[] { "edge-a", "edge-b", "edge-cost" }, impact.AffectedRelationships.ToArray());
    }

    [Fact]
    public void RemovedEntityAndIsolatedProvenanceEditAreRetained()
    {
        var old = Build("a", Scope("a", "x", "z"));
        var nodes = Entities.Where(n => n.Id != "z").Select(n => n.Id == "outside" ? n with { Source = Source with { MappingVersion = "map2" } } : n);
        var next = DecisionGraphSnapshot.Create(Base with { RevisionId = "b" }, nodes, [Scope("a", "x")]);
        var result = RevisionImpact.Compare(old, next);
        Assert.Equal(new[] { "outside", "z" }, result.ChangedEntities.ToArray());
        Assert.Equal(new[] { "a", "outside", "x", "z" }, result.AffectedEntities.ToArray());
    }

    [Fact]
    public void ReorderingIsStableAndFrozenIdentityCannotBeReusedForNewContent()
    {
        var snapshot = Build("same", Scope("a", "x", "y"));
        var reordered = Build("same", Scope("a", "y", "x"));
        Assert.Empty(RevisionImpact.Compare(snapshot, reordered).AffectedRelationships);
        Assert.Equal("graph.revisionConflict", Assert.Throws<GraphValidationException>(() =>
            RevisionImpact.Compare(snapshot, Build("same", Scope("a", "x")))).Code);
        foreach (var other in new[] { Base with { TenantId = "another" }, Base with { ModelId = "another" } })
            Assert.Equal("graph.identity", Assert.Throws<GraphValidationException>(() =>
                RevisionImpact.Compare(snapshot, DecisionGraphSnapshot.Create(other, Entities, []))).Code);
    }

    [Fact]
    public void MetadataChangesIncludeOldAndNewEndpoints()
    {
        var one = Scope("a", "x");
        var two = one with { OwnerId = "b", Source = Source with { RecordKey = "other" } };
        var diff = RevisionImpact.Compare(Build("a", one), Build("b", two));
        Assert.Equal(new[] { "a", "b", "x" }, diff.AffectedEntities.ToArray());
        Assert.Equal("edge-a", Assert.Single(diff.ChangedRelationships));
    }

    [Fact]
    public void SeededEditsAgreeWithIndependentFullScan()
    {
        var random = new Random(20261127);
        var vars = Enumerable.Range(0, 6).Select(i => "x" + i).ToArray();
        var owners = Enumerable.Range(0, 4).Select(i => "c" + i).ToArray();
        var nodes = vars.Select(v => new GraphEntity(v, EntityKind.Variable, Source))
            .Concat(owners.Select(c => new GraphEntity(c, EntityKind.Constraint, Source))).ToArray();
        for (var trial = 0; trial < 128; trial++)
        {
            RelationshipInput[] Sample() => owners.Where(_ => random.Next(4) != 0)
                .Select(c => Scope(c, vars.Where(_ => random.Next(3) == 0).ToArray())).ToArray();
            var left = Sample();
            var right = Sample();
            var before = DecisionGraphSnapshot.Create(Base, nodes, left);
            var after = DecisionGraphSnapshot.Create(Base with { RevisionId = "r2" }, nodes, right);
            var actual = RevisionImpact.Compare(before, after);
            // Oracle works directly on independently generated input lists, without the production incidence index.
            var changed = owners.Where(c =>
            {
                var a = left.SingleOrDefault(e => e.OwnerId == c);
                var b = right.SingleOrDefault(e => e.OwnerId == c);
                return a is null ? b is not null : b is null || !a.Targets.SequenceEqual(b.Targets);
            }).Select(c => "edge-" + c).ToHashSet();
            Assert.Equal(changed.Order(StringComparer.Ordinal), actual.ChangedRelationships);
            var vertices = new HashSet<string>();
            var relations = new HashSet<string>(changed);
            int previous;
            do
            {
                previous = vertices.Count + relations.Count;
                foreach (var e in left.Concat(right))
                {
                    var scope = e.Targets.Append(e.OwnerId);
                    if (!relations.Contains(e.Id) && !scope.Any(vertices.Contains)) continue;
                    relations.Add(e.Id);
                    vertices.UnionWith(scope);
                }
            } while (previous != vertices.Count + relations.Count);
            Assert.Equal(vertices.Order(StringComparer.Ordinal), actual.AffectedEntities);
            Assert.Equal(relations.Order(StringComparer.Ordinal), actual.AffectedRelationships);
        }
    }
}
