using System.Collections.Immutable;
using WowDi.Contracts;
using WowDi.Dml.Semantics;
using WowDi.Udir;

namespace WowDi.Dml.Lowering;

/// <summary>Exact index and scale reconstruction, not independent solution verification.</summary>
public static class NativeMapping
{
    public static ImmutableDictionary<string,string> RestoreAssignment(LoweredModel model,IReadOnlyDictionary<int,string> assignment)
    {
        ArgumentNullException.ThrowIfNull(model);ArgumentNullException.ThrowIfNull(assignment);
        if(assignment.Count!=model.Columns.Length)throw Error("assignment");
        var result=ImmutableDictionary.CreateBuilder<string,string>(StringComparer.Ordinal);
        foreach(var column in model.Columns)
        {
            if(!assignment.TryGetValue(column.Index,out var raw))throw Error("assignment");
            var n=Parse(raw);
            if(column.Kind==ScalarKind.Boolean)
            {
                if(n!=Rational.Origin&&n!=Rational.One)throw Error("integrality");
                result.Add(column.SymbolId,n.Zero?"false":"true");
            }
            else
            {
                if((column.Kind is ScalarKind.Integer or ScalarKind.Instant)&&!n.Integral)throw Error("integrality");
                result.Add(column.SymbolId,n.ToString());
            }
        }
        return result.ToImmutable();
    }

    /// <summary>Input is the full scaled objective including the recorded offset.</summary>
    public static string RestoreObjective(LoweredModel model,string fullScaledObjective)
    {
        ArgumentNullException.ThrowIfNull(model);
        if(model.Objective is not {} objective)throw Error("objective");
        try{return (Parse(fullScaledObjective)/Parse(objective.Scale)).ToString();}
        catch(OverflowException){throw Error("overflow");}
    }
    private static Rational Parse(string raw)
    {
        if(raw is null||raw.Length>514||raw.Count(c=>c=='/')>1)throw Error("number");
        try
        {
            var n=Rational.Parse(raw);
            if(n.ToString()!=raw)throw Error("number");
            return n;
        }
        catch(Exception e) when(e is FormatException or DivideByZeroException or OverflowException)
        {throw Error("number");}
    }
    private static ContractException Error(string field)=>new("lower.map."+field,"Invalid native mapping: "+field+".");
}
