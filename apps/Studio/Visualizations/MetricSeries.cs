using System.Collections.Immutable;
using System.Globalization;
namespace WowDi.Studio.Visualizations;

// Consumer-owned event identity; never infer an event or a metric from a missing observation.
public sealed record MetricObservation(string EventId, long Sequence, decimal? Value);
public sealed record MetricPoint(MetricObservation Observation, double X, double? Y);
public sealed record MetricSeries(string RunId, string Metric, string Unit, ImmutableArray<MetricPoint> Points,
    ImmutableArray<ImmutableArray<MetricPoint>> Segments)
{
    public static MetricSeries Create(string runId,string metric,string unit,IEnumerable<MetricObservation> observations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(observations);
        var items=observations.OrderBy(o=>o.Sequence).ToArray();
        if(items.Any(o=>string.IsNullOrWhiteSpace(o.EventId)||o.Sequence<0) ||
           items.Select(o=>o.EventId).Distinct(StringComparer.Ordinal).Count()!=items.Length ||
           items.Select(o=>o.Sequence).Distinct().Count()!=items.Length)
            throw new ArgumentException("Metric event IDs and nonnegative sequences must be unique.",nameof(observations));
        var values=items.Where(o=>o.Value.HasValue).Select(o=>o.Value!.Value).ToArray();
        var min=values.Length==0?0:values.Min();
        var max=values.Length==0?0:values.Max();
        double Normalize(decimal value)
        {
            if(max==min) return .5;
            try { return (double)((value-min)/(max-min)); }
            catch(OverflowException) { return (double)((value/4-min/4)/(max/4-min/4)); }
        }
        var first=items.Length==0?0:items[0].Sequence;
        var last=items.Length==0?0:items[^1].Sequence;
        var points=items.Select(o=>new MetricPoint(o,last==first?40:40+520*(double)(o.Sequence-first)/(last-first),
            o.Value is null?null:180-160*Normalize(o.Value.Value))).ToImmutableArray();
        var segments=ImmutableArray.CreateBuilder<ImmutableArray<MetricPoint>>();
        var current=ImmutableArray.CreateBuilder<MetricPoint>();
        long? previous=null;
        foreach(var point in points)
        {
            if(point.Y is null || (previous.HasValue && point.Observation.Sequence-previous.Value!=1))
            {
                if(current.Count>0){segments.Add(current.ToImmutable());current.Clear();}
            }
            if(point.Y is not null) current.Add(point);
            previous=point.Observation.Sequence;
        }
        if(current.Count>0)segments.Add(current.ToImmutable());
        return new(runId,metric,unit,points,segments.ToImmutable());
    }
    public static string Coordinate(double value)=>value.ToString("0.###",CultureInfo.InvariantCulture);
    public static string Path(ImmutableArray<MetricPoint> segment)=>string.Join(" ",segment.Select(p=>$"{Coordinate(p.X)},{Coordinate(p.Y!.Value)}"));
}
