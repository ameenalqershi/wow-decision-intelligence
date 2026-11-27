using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using WowDi.Manufacturing.Data;

namespace WowDi.Manufacturing.Dispatch;
public enum DispatchOutcome { Candidate, NoCandidate }
public enum DispatchFailureKind { MaterialAvailability, CalendarOrHorizon }
public sealed record DispatchAssignment(string OperationId,string MachineId,int StartMinute,int EndMinute);
public sealed record DispatchFailure(DispatchFailureKind Kind,string OperationId,string? MaterialId,int? EarliestMinute,int PlacedCount);
public sealed record DispatchResult(string AlgorithmVersion,string RevisionId,string SourceDigest,string ObjectiveUnit,
    DispatchOutcome Outcome,ImmutableArray<DispatchAssignment> Assignments,string? ObjectiveValue,
    ImmutableArray<string> DispatchOrder,DispatchFailure? Failure);

// A deterministic serial earliest-due-date baseline, not a solver adapter,
// verifier or optimality certificate. Candidate still needs independent S04.
public static class EarliestDueDate
{
    public const string Version="edd-serial-insertion/1";
    public static DispatchResult Schedule(FrozenFactoryDataset source,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();
        var f=source.Dataset;
        var operations=f.Operations.ToDictionary(o=>o.OperationId,StringComparer.Ordinal);
        var pending=operations.Keys.ToDictionary(id=>id,_=>0,StringComparer.Ordinal);
        var earliest=operations.ToDictionary(k=>k.Key,k=>k.Value.ReleaseMinute,StringComparer.Ordinal);
        var successors=operations.Keys.ToDictionary(id=>id,_=>new List<string>(),StringComparer.Ordinal);
        foreach(var edge in f.Precedence){pending[edge.Successor]++;successors[edge.Predecessor].Add(edge.Successor);}
        var ready=new SortedSet<(int Due,string Id)>(Comparer<(int Due,string Id)>.Create((a,b)=>
        {int c=a.Due.CompareTo(b.Due);return c!=0?c:StringComparer.Ordinal.Compare(a.Id,b.Id);}));
        foreach(var op in f.Operations)if(pending[op.OperationId]==0)ready.Add((op.DueMinute,op.OperationId));
        var machines=f.Calendars.ToDictionary(c=>c.MachineId,c=>
            new SortedSet<(int Start,int End)>(c.Downtime.Select(d=>(d.StartMinute,d.EndMinute))),StringComparer.Ordinal);
        var stock=f.Materials.ToDictionary(m=>m.MaterialId,m=>new StockTimeline(Q.Parse(m.InitialQuantity)),StringComparer.Ordinal);
        foreach(var receipt in f.Receipts)stock[receipt.MaterialId].Change(receipt.AvailableMinute,Q.Parse(receipt.Quantity));
        var assigned=ImmutableArray.CreateBuilder<DispatchAssignment>();
        var order=ImmutableArray.CreateBuilder<string>();
        Q objective=Q.Zero;
        DispatchResult Failed(DispatchFailureKind kind,string id,string? material,int? tick)=>
            new(Version,f.RevisionId,source.Sha256,f.ObjectiveUnit,DispatchOutcome.NoCandidate,[],null,
                order.ToImmutable(),new(kind,id,material,tick,assigned.Count));
        while(ready.Count>0)
        {
            ct.ThrowIfCancellationRequested();
            var item=ready.Min;ready.Remove(item);var op=operations[item.Id];
            int lower=earliest[item.Id];
            // Order independent of input material row order, including failure witness.
            foreach(var need in op.Materials.OrderBy(m=>m.MaterialId,StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var first=stock[need.MaterialId].EarliestSufficient(Q.Parse(need.Quantity),ct);
                if(first is null)return Failed(DispatchFailureKind.MaterialAvailability,item.Id,need.MaterialId,null);
                lower=Math.Max(lower,first.Value);
            }
            int deadline=Math.Min(f.Clock.HorizonMinutes,op.HardDeadlineMinute??f.Clock.HorizonMinutes);
            var busy=machines[op.MachineId];
            int start=lower;
            foreach(var (a,b) in busy)
            {
                ct.ThrowIfCancellationRequested();
                if(b<=start)continue;
                if((long)start+op.DurationMinutes<=a)break;
                start=b;
            }
            if((long)start+op.DurationMinutes>deadline)
                return Failed(DispatchFailureKind.CalendarOrHorizon,item.Id,null,start);
            int end=checked(start+op.DurationMinutes);
            busy.Add((start,end));
            foreach(var need in op.Materials)stock[need.MaterialId].Change(start,-Q.Parse(need.Quantity));
            assigned.Add(new(item.Id,op.MachineId,start,end));order.Add(item.Id);
            objective+=Q.Parse(op.TardinessWeight)*Math.Max(0,end-op.DueMinute);
            foreach(var next in successors[item.Id])
            {
                earliest[next]=Math.Max(earliest[next],end);
                if(--pending[next]==0)ready.Add((operations[next].DueMinute,next));
            }
        }
        // Admission rejects cycles; a mismatch is an invariant violation, not
        // a successful partial result.
        if(assigned.Count!=operations.Count)throw new InvalidOperationException("Incomplete precedence traversal.");
        return new(Version,f.RevisionId,source.Sha256,f.ObjectiveUnit,DispatchOutcome.Candidate,
            assigned.OrderBy(a=>a.OperationId,StringComparer.Ordinal).ToImmutableArray(),objective.ToString(),order.ToImmutable(),null);
    }

    // The existing plan's prefix balance is piecewise constant. Inserting
    // consumption reduces *every* later balance. Find the end of the last
    // insufficient interval, not merely the first currently sufficient tick.
    private sealed class StockTimeline(Q initial)
    {
        private readonly SortedDictionary<int,Q> deltas=new(){{0,Q.Zero}};
        public void Change(int tick,Q delta)=>deltas[tick]=deltas.GetValueOrDefault(tick,Q.Zero)+delta;
        public int? EarliestSufficient(Q quantity,CancellationToken ct)
        {
            Q balance=initial;
            int? first=0;
            foreach(var (tick,delta) in deltas)
            {
                ct.ThrowIfCancellationRequested();
                balance+=delta;
                if(balance.CompareTo(quantity)<0)first=null;
                else if(first is null)first=tick;
            }
            return first;
        }
    }
    // Baseline-only exact stock and cost arithmetic. V01 already validates
    // canonical bounded UDIR number strings. No rounding or solver lowering.
    private readonly record struct Q(BigInteger N,BigInteger D):IComparable<Q>
    {
        public static Q Zero=>new(BigInteger.Zero,BigInteger.One);
        public static Q Parse(string s)
        {
            var parts=s.Split('/');
            return new(BigInteger.Parse(parts[0],CultureInfo.InvariantCulture),
                parts.Length==1?BigInteger.One:BigInteger.Parse(parts[1],CultureInfo.InvariantCulture));
        }
        private static Q Reduce(BigInteger n,BigInteger d)
        {var g=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);return new(n/g,d/g);}
        public static Q operator +(Q a,Q b)
        {
            var g=BigInteger.GreatestCommonDivisor(a.D,b.D);
            return Reduce(a.N*(b.D/g)+b.N*(a.D/g),a.D*(b.D/g));
        }
        public static Q operator -(Q q)=>new(-q.N,q.D);
        public static Q operator *(Q q,int value)=>Reduce(q.N*value,q.D);
        public int CompareTo(Q other)=>(N*other.D).CompareTo(other.N*D);
        public override string ToString()=>D.IsOne?N.ToString(CultureInfo.InvariantCulture):FormattableString.Invariant($"{N}/{D}");
    }
}
