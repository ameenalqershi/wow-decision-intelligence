using WowDi.Dml.Lowering;
using WowDi.Udir;

namespace WowDi.Research.Sparse;

/// <summary>Independent conversion of the existing exact lowering (S06/M08 LoweredModel) into the sparse form, used only
/// for equivalence testing against the UDIR reference. Rows keep the lowered scale: Σ a·x (rel) rhs. The objective is
/// divided back by its recorded scale, so its value equals the original UDIR objective.</summary>
public static class UdirBridge
{
    public static (SparseModel Model,string[] SymbolIds) FromLowered(LoweredModel lowered,string name)
    {
        var o=lowered.Objective;
        var b=new SparseBuilder(name,o?.Direction==Direction.Maximize);
        var scale=o is null?Q.One:Q.Parse(o.Scale);
        var cost=new Dictionary<int,Q>();
        if(o is not null)foreach(var t in o.Terms)cost[t.Column]=Q.Parse(t.Value)/scale;
        var ids=new string[lowered.Columns.Length];
        foreach(var c in lowered.Columns.OrderBy(c=>c.Index))
        {
            if(c.Index!=b.Columns)throw new SparseException("bridge.columnOrder");
            b.AddColumn(Q.Parse(c.Lower),Q.Parse(c.Upper),c.Kind is ScalarKind.Integer or ScalarKind.Boolean or ScalarKind.Instant,cost.GetValueOrDefault(c.Index,Q.Zero));
            ids[c.Index]=c.SymbolId;
        }
        if(o is not null)b.SetObjectiveOffset(Q.Parse(o.Offset)/scale);
        foreach(var r in lowered.Rows.OrderBy(r=>r.Index))
        {
            var rhs=Q.Parse(r.RightHandSide);
            var cols=r.Terms.Select(t=>t.Column).ToArray();var vals=r.Terms.Select(t=>Q.Parse(t.Value)).ToArray();
            switch(r.Relation)
            {
                case Operation.LessOrEqual:b.AddRow(cols,vals,null,rhs);break;
                case Operation.GreaterOrEqual:b.AddRow(cols,vals,rhs,null);break;
                case Operation.Equal:b.AddRow(cols,vals,rhs,rhs);break;
                default:throw new SparseException("bridge.relation");
            }
        }
        return (b.Build(),ids);
    }
}
