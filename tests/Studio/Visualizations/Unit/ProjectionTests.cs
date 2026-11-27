using WowDi.DecisionGraph;
using Xunit;
namespace WowDi.Studio.Visualizations.Tests;

public sealed class ProjectionTests
{
    private static readonly SourceStamp Source=new("fixture","record","v1",DateTimeOffset.UnixEpoch);
    [Fact]
    public void HyperedgeKeepsRevisionIdentityOwnerAndEveryTarget()
    {
        var snapshot=DecisionGraphSnapshot.Create(new("tenant","model","revision"),
            [new("owner",EntityKind.Decision,Source),new("a",EntityKind.Resource,Source),new("b",EntityKind.Resource,Source)],
            [new("h",RelationKind.Consumes,"owner",["b","a"],Source)]);
        var view=GraphProjection.Create(snapshot);
        Assert.Equal(snapshot.Revision,view.Revision);
        var edge=Assert.Single(view.Edges);
        Assert.Equal("owner",edge.OwnerId);
        Assert.Equal(RelationKind.Consumes,edge.Kind);
        Assert.Equal(new[]{"a","b"},edge.Targets);
        Assert.Equal(new[]{"owner","a","b"},view.Incidence.Select(i=>i.EntityId));
        Assert.Single(view.Incidence,i=>i.IsOwner);
        Assert.All(view.Incidence,i=>Assert.Equal(edge.Id,i.EdgeId));
    }
    [Fact]
    public void EmptyScopeAndEmptyGraphAreRepresentable()
    {
        var empty=GraphProjection.Create(DecisionGraphSnapshot.Create(new("t","m","r"),[],[]));
        Assert.Empty(empty.Nodes);Assert.Empty(empty.Incidence);Assert.True(empty.Height>0);
        var graph=GraphProjection.Create(DecisionGraphSnapshot.Create(new("t","m","r"),[new("c",EntityKind.Constraint,Source)],
            [new("scope",RelationKind.ConstraintScope,"c",[],Source)]));
        Assert.Empty(Assert.Single(graph.Edges).Targets);
        Assert.True(Assert.Single(graph.Incidence).IsOwner);
    }
    [Fact]
    public void NullAndAbsentSequenceBreakLinesWithoutCreatingEvents()
    {
        var series=MetricSeries.Create("run","Cost","USD",
            [new("e6",6,1),new("e1",1,9),new("e2",2,7),new("e3",3,null),new("e4",4,4),new("e7",7,0)]);
        Assert.Equal(new[]{"e1","e2","e3","e4","e6","e7"},series.Points.Select(p=>p.Observation.EventId));
        Assert.Null(series.Points[2].Y);
        Assert.Equal(new[]{2,1,2},series.Segments.Select(s=>s.Length));
        Assert.All(series.Segments,s=>Assert.All(s,p=>Assert.NotNull(p.Observation.Value)));
    }
    [Fact]
    public void ZeroAndAllMissingAreNotConfused()
    {
        var zero=MetricSeries.Create("r","m","unit",[new("e",0,0)]);
        Assert.Equal(100,Assert.Single(zero.Points).Y);
        var missing=MetricSeries.Create("r","m","unit",[new("e",0,null)]);
        Assert.Null(Assert.Single(missing.Points).Y);Assert.Empty(missing.Segments);
        Assert.Empty(MetricSeries.Create("r","m","unit",[]).Points);
    }
    [Fact]
    public void LargeDecimalsAndExtremeRangeDoNotCollapseOrOverflow()
    {
        var close=MetricSeries.Create("r","m","u",[new("a",0,decimal.MaxValue-1),new("b",1,decimal.MaxValue)]);
        Assert.Equal(180,close.Points[0].Y);Assert.Equal(20,close.Points[1].Y);
        var wide=MetricSeries.Create("r","m","u",[new("a",0,decimal.MinValue),new("b",1,decimal.MaxValue)]);
        Assert.Equal(180,wide.Points[0].Y);Assert.Equal(20,wide.Points[1].Y);
    }
    [Fact]
    public void InvalidEventIdentityFailsClosed()
    {
        Assert.Throws<ArgumentException>(()=>MetricSeries.Create("r","m","u",[new("a",1,2),new("a",2,3)]));
        Assert.Throws<ArgumentException>(()=>MetricSeries.Create("r","m","u",[new("a",1,2),new("b",1,3)]));
        Assert.Throws<ArgumentException>(()=>MetricSeries.Create("r","m","u",[new("a",-1,2)]));
    }
}
