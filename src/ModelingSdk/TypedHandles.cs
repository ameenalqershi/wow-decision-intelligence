using WowDi.Udir;

namespace WowDi.ModelingSdk;

/// <summary>Implement with a marker struct for each quantity/unit or currency type.</summary>
public interface IQuantity { }

public sealed class QuantityType<T> where T : struct, IQuantity
{
    internal ModelBuilder Owner { get; }
    public ScalarType Descriptor { get; }
    internal QuantityType(ModelBuilder owner, ScalarType descriptor) => (Owner, Descriptor) = (owner, descriptor);
}

public sealed class QuantityExpression<T> where T : struct, IQuantity
{
    public QuantityType<T> Type { get; }
    public string Id { get; }
    internal QuantityExpression(QuantityType<T> type, string id) => (Type, Id) = (type, id);
}

public sealed class QuantitySymbol<T> where T : struct, IQuantity
{
    public QuantityType<T> Type { get; }
    public string Id { get; }
    internal QuantitySymbol(QuantityType<T> type, string id) => (Type, Id) = (type, id);
}

public sealed class BooleanExpression
{
    internal ModelBuilder Owner { get; }
    public string Id { get; }
    internal BooleanExpression(ModelBuilder owner, string id) => (Owner, Id) = (owner, id);
}

public sealed class BooleanSymbol
{
    internal ModelBuilder Owner { get; }
    public string Id { get; }
    internal BooleanSymbol(ModelBuilder owner, string id) => (Owner, Id) = (owner, id);
}

public sealed class ScenarioOverride
{
    internal ModelBuilder Owner { get; }
    internal ScenarioValue Value { get; }
    internal ScenarioOverride(ModelBuilder owner, ScenarioValue value) => (Owner, Value) = (owner, value);
}
