using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.ModelImport;
using WowDi.Udir;

namespace WowDi.ModelingSdk;

/// <summary>Single-writer, explicit source-linked UDIR construction. No evaluation or compilation.</summary>
public sealed class ModelBuilder
{
    private readonly string tenantId, modelId, revisionId;
    private readonly TimeBasis basis;
    private readonly MeasurementSemantics measurements;
    private readonly Dictionary<string, ScalarType> types = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> quantityMarkers = [];
    private readonly List<Symbol> symbols = [];
    private readonly List<Expression> expressions = [];
    private readonly List<Constraint> constraints = [];
    private readonly List<Objective> objectives = [];
    private readonly List<Scenario> scenarios = [];
    private readonly List<string> requires = [];
    private readonly Dictionary<string, SourceDocument> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableArray<byte>> inputs = new(StringComparer.Ordinal);
    private const string BooleanId = "sdk_bool";
    private BuiltModel? frozen;

    public ModelBuilder(string tenantId, string modelId, string revisionId, TimeBasis basis,
        FrozenMeasurementCatalog catalog, ModelSource source)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(catalog);
        this.tenantId = tenantId; this.modelId = modelId; this.revisionId = revisionId;
        this.basis = basis; this.measurements = catalog.Semantics;
        AddInput(catalog.Input);
        AddSource(source);
        types.Add(BooleanId, new(BooleanId, ScalarKind.Boolean));
    }

    public void AddInput(FrozenInput input)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(input);
        if (inputs.TryGetValue(input.Digest, out var prior) && !prior.AsSpan().SequenceEqual(input.Bytes.AsSpan()))
            throw Error("digest");
        inputs[input.Digest] = input.Bytes;
    }
    public void AddSource(ModelSource source)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(source);
        if (sources.ContainsKey(source.Document.Id)) throw Error("source");
        AddInput(source.Input);
        sources.Add(source.Document.Id, source.Document);
    }

    public QuantityType<T> Integer<T>(string id, string? unit = null) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Integer, unit));
    public QuantityType<T> Rational<T>(string id, string? unit = null) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Rational, unit));
    public QuantityType<T> Quantity<T>(string id, string unit) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Quantity, unit));
    public QuantityType<T> Rate<T>(string id, string unit) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Rate, unit));
    public QuantityType<T> Money<T>(string id, string currency) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Money, Currency: currency));
    public QuantityType<T> Duration<T>(string id, string unit) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Duration, unit));
    public QuantityType<T> Instant<T>(string id, string unit) where T : struct, IQuantity => Register<T>(new(id, ScalarKind.Instant, unit));

    private QuantityType<T> Register<T>(ScalarType type) where T : struct, IQuantity
    {
        EnsureMutable();
        if (quantityMarkers.ContainsKey(typeof(T)) || !types.TryAdd(type.Id, type)) throw Error("typeRegistration");
        quantityMarkers.Add(typeof(T), type.Id);
        return new(this, type);
    }

    public QuantitySymbol<T> Variable<T>(string id, QuantityType<T> type, ExactValue? lower, ExactValue? upper, SourceSpan span)
        where T : struct, IQuantity => Declare(id, type, SymbolRole.Variable, lower, upper, span);
    public QuantitySymbol<T> Parameter<T>(string id, QuantityType<T> type, ExactValue value, SourceSpan span)
        where T : struct, IQuantity => Declare(id, type, SymbolRole.Parameter, value, value, span);
    public QuantitySymbol<T> Parameter<T>(string id, QuantityType<T> type, ExactValue? lower, ExactValue? upper, SourceSpan span)
        where T : struct, IQuantity => Declare(id, type, SymbolRole.Parameter, lower, upper, span);
    public QuantitySymbol<T> Resource<T>(string id, QuantityType<T> type, ExactValue? lower, ExactValue? upper, SourceSpan span)
        where T : struct, IQuantity => Declare(id, type, SymbolRole.Resource, lower, upper, span);

    private QuantitySymbol<T> Declare<T>(string id, QuantityType<T> type, SymbolRole role, ExactValue? lower, ExactValue? upper, SourceSpan span)
        where T : struct, IQuantity
    {
        EnsureMutable();
        Own(type.Owner);
        symbols.Add(new(id, role, type.Descriptor.Id, span, lower?.ToString(), upper?.ToString()));
        return new(type, id);
    }

    public BooleanSymbol BooleanVariable(string id, SourceSpan span) => DeclareBoolean(id, SymbolRole.Variable, span);
    public BooleanSymbol BooleanParameter(string id, SourceSpan span) => DeclareBoolean(id, SymbolRole.Parameter, span);
    public BooleanSymbol BooleanResource(string id, SourceSpan span) => DeclareBoolean(id, SymbolRole.Resource, span);
    private BooleanSymbol DeclareBoolean(string id, SymbolRole role, SourceSpan span)
    {
        EnsureMutable();
        symbols.Add(new(id, role, BooleanId, span));
        return new(this, id);
    }
    public QuantityExpression<T> Reference<T>(QuantitySymbol<T> symbol, SourceSpan span) where T : struct, IQuantity
        => Node(symbol.Type, Operation.Symbol, [], span, symbolId: symbol.Id);
    public BooleanExpression Reference(BooleanSymbol symbol, SourceSpan span)
    {
        Own(symbol.Owner);
        return new(this, Append(Operation.Symbol, BooleanId, [], span, symbolId: symbol.Id));
    }
    public QuantityExpression<T> Literal<T>(QuantityType<T> type, ExactValue value, SourceSpan span) where T : struct, IQuantity
        => Node(type, Operation.Literal, [], span, literal: value.ToString());
    /// <summary>Normalize a supplied exact value, delegating all scale/dimension checks to M04.</summary>
    public QuantityExpression<TTo> ConvertedLiteral<TFrom, TTo>(QuantityType<TFrom> from, QuantityType<TTo> to,
        ExactValue value, SourceSpan span)
        where TFrom : struct, IQuantity where TTo : struct, IQuantity
    {
        Own(from.Owner); Own(to.Owner);
        if (from.Descriptor.Kind is ScalarKind.Instant or ScalarKind.Money ||
            to.Descriptor.Kind is ScalarKind.Instant or ScalarKind.Money ||
            from.Descriptor.Unit is null || to.Descriptor.Unit is null)
            throw Error("conversionType");
        var converted = measurements.ConvertUnit(value.ToString(), from.Descriptor.Unit, to.Descriptor.Unit);
        return Node(to, Operation.Literal, [], span, literal: converted);
    }

    /// <summary>Explicit FX conversion, recording the actual versioned receipt among the frozen inputs.</summary>
    public QuantityExpression<TTo> CurrencyLiteral<TFrom, TTo>(QuantityType<TFrom> from, QuantityType<TTo> to,
        ExactValue value, FxSnapshot snapshot, DateTimeOffset asOf, SourceSpan span)
        where TFrom : struct, IQuantity where TTo : struct, IQuantity
    {
        Own(from.Owner); Own(to.Owner);
        if (from.Descriptor.Kind != ScalarKind.Money || to.Descriptor.Kind != ScalarKind.Money)
            throw Error("conversionType");
        var receipt = measurements.ConvertCurrency(value.ToString(), from.Descriptor.Currency!, to.Descriptor.Currency!, snapshot, asOf);
        AddInput(new FrozenInput(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "wowdi.sdk.fx-receipt/1",
            input = value.ToString(),
            amount = receipt.Amount,
            currency = receipt.Currency,
            snapshot = receipt.Snapshot,
            asOf = receipt.AsOf
        })));
        return Node(to, Operation.Literal, [], span, literal: receipt.Amount);
    }

    public BooleanExpression Literal(bool value, SourceSpan span)
        => new(this, Append(Operation.Literal, BooleanId, [], span, literal: value ? "true" : "false"));

    public QuantityExpression<T> Add<T>(QuantityExpression<T> left, QuantityExpression<T> right, SourceSpan span) where T : struct, IQuantity
        => Same(left, right, Operation.Add, span);
    public QuantityExpression<T> Subtract<T>(QuantityExpression<T> left, QuantityExpression<T> right, SourceSpan span) where T : struct, IQuantity
        => Same(left, right, Operation.Subtract, span);
    public QuantityExpression<T> Negate<T>(QuantityExpression<T> value, SourceSpan span) where T : struct, IQuantity
        => Node(value.Type, Operation.Negate, [value.Id], span);
    private QuantityExpression<T> Same<T>(QuantityExpression<T> left, QuantityExpression<T> right, Operation op, SourceSpan span) where T : struct, IQuantity
    {
        Own(left.Type.Owner); Own(right.Type.Owner);
        return Node(left.Type, op, [left.Id, right.Id], span);
    }
    public QuantityExpression<TResult> Multiply<TLeft, TRight, TResult>(QuantityExpression<TLeft> left, QuantityExpression<TRight> right, QuantityType<TResult> result, SourceSpan span)
        where TLeft : struct, IQuantity where TRight : struct, IQuantity where TResult : struct, IQuantity
        => Binary(left, right, result, Operation.Multiply, span);
    public QuantityExpression<TResult> Divide<TLeft, TRight, TResult>(QuantityExpression<TLeft> left, QuantityExpression<TRight> right, QuantityType<TResult> result, SourceSpan span)
        where TLeft : struct, IQuantity where TRight : struct, IQuantity where TResult : struct, IQuantity
        => Binary(left, right, result, Operation.Divide, span);
    public QuantityExpression<TInstant> Shift<TInstant, TDuration>(QuantityExpression<TInstant> instant, QuantityExpression<TDuration> duration, SourceSpan span, bool subtract = false)
        where TInstant : struct, IQuantity where TDuration : struct, IQuantity
    {
        if (instant.Type.Descriptor.Kind != ScalarKind.Instant || duration.Type.Descriptor.Kind != ScalarKind.Duration)
            throw Error("timeOperation");
        return Binary(instant, duration, instant.Type, subtract ? Operation.Subtract : Operation.Add, span);
    }
    public QuantityExpression<TDuration> Difference<TInstant, TDuration>(QuantityExpression<TInstant> left, QuantityExpression<TInstant> right, QuantityType<TDuration> result, SourceSpan span)
        where TInstant : struct, IQuantity where TDuration : struct, IQuantity
    {
        if (left.Type.Descriptor.Kind != ScalarKind.Instant || result.Descriptor.Kind != ScalarKind.Duration)
            throw Error("timeOperation");
        return Binary(left, right, result, Operation.Subtract, span);
    }
    private QuantityExpression<TResult> Binary<TLeft, TRight, TResult>(QuantityExpression<TLeft> left, QuantityExpression<TRight> right, QuantityType<TResult> result, Operation op, SourceSpan span)
        where TLeft : struct, IQuantity where TRight : struct, IQuantity where TResult : struct, IQuantity
    {
        Own(left.Type.Owner); Own(right.Type.Owner);
        return Node(result, op, [left.Id, right.Id], span);
    }
    public BooleanExpression Equal<T>(QuantityExpression<T> left, QuantityExpression<T> right, SourceSpan span) where T : struct, IQuantity
        => Compare(left, right, Operation.Equal, span);
    public BooleanExpression AtMost<T>(QuantityExpression<T> left, QuantityExpression<T> right, SourceSpan span) where T : struct, IQuantity
        => Compare(left, right, Operation.LessOrEqual, span);
    public BooleanExpression AtLeast<T>(QuantityExpression<T> left, QuantityExpression<T> right, SourceSpan span) where T : struct, IQuantity
        => Compare(left, right, Operation.GreaterOrEqual, span);
    private BooleanExpression Compare<T>(QuantityExpression<T> left, QuantityExpression<T> right, Operation op, SourceSpan span) where T : struct, IQuantity
    {
        Own(left.Type.Owner); Own(right.Type.Owner);
        return new(this, Append(op, BooleanId, [left.Id, right.Id], span));
    }
    public BooleanExpression Equal(BooleanExpression left, BooleanExpression right, SourceSpan span)
    {
        Own(left.Owner); Own(right.Owner);
        return new(this, Append(Operation.Equal, BooleanId, [left.Id, right.Id], span));
    }

    public void HardConstraint(string id, BooleanExpression predicate, SourceSpan span)
    {
        EnsureMutable(); Own(predicate.Owner); constraints.Add(new(id, predicate.Id, true, span));
    }
    public void SoftConstraint(string id, BooleanExpression predicate, ExactValue penalty, SourceSpan span)
    {
        EnsureMutable(); Own(predicate.Owner); constraints.Add(new(id, predicate.Id, false, span, penalty.ToString()));
    }
    public void Optimize<T>(string id, QuantityExpression<T> expression, Direction direction, int priority,
        SourceSpan span, ExactValue? weight = null, ExactValue? tolerance = null) where T : struct, IQuantity
    {
        EnsureMutable(); Own(expression.Type.Owner);
        objectives.Add(new(id, expression.Id, direction, priority, (weight ?? (ExactValue)1).ToString(),
            (tolerance ?? default(ExactValue)).ToString(), span));
    }
    public ScenarioOverride Override<T>(QuantitySymbol<T> symbol, ExactValue value) where T : struct, IQuantity
    {
        Own(symbol.Type.Owner); return new(this, new(symbol.Id, value.ToString()));
    }
    public ScenarioOverride Override(BooleanSymbol symbol, bool value)
    {
        Own(symbol.Owner); return new(this, new(symbol.Id, value ? "true" : "false"));
    }
    public void AddScenario(string id, int stage, string informationSet, SourceSpan span,
        string? parentId = null, ExactValue? probability = null, params ScenarioOverride[] overrides)
    {
        EnsureMutable();
        foreach (var item in overrides) Own(item.Owner);
        scenarios.Add(new(id, stage, informationSet, span, [.. overrides.Select(o => o.Value)], parentId, probability?.ToString()));
    }
    public void RequireCapability(string capability) { EnsureMutable(); requires.Add(capability); }

    private QuantityExpression<T> Node<T>(QuantityType<T> type, Operation op, ImmutableArray<string> args, SourceSpan span, string? literal = null, string? symbolId = null) where T : struct, IQuantity
    {
        Own(type.Owner); return new(type, Append(op, type.Descriptor.Id, args, span, literal, symbolId));
    }
    private string Append(Operation op, string typeId, ImmutableArray<string> args, SourceSpan span, string? literal = null, string? symbolId = null)
    {
        EnsureMutable();
        var id = "e" + (expressions.Count + 1).ToString("D8", CultureInfo.InvariantCulture);
        expressions.Add(new(id, op, typeId, args, span, literal, symbolId)); return id;
    }
    private void EnsureMutable()
    {
        if (frozen is not null) throw Error("frozen");
    }
    private void Own(ModelBuilder owner)
    {
        if (!ReferenceEquals(owner, this)) throw Error("foreignHandle");
    }
    private static ContractException Error(string code) => new("sdk." + code, "Invalid modeling SDK operation: " + code + ".");

    public BuiltModel Build()
    {
        if (frozen is not null) return frozen;
        var doc = UdirDocument.Create(new(tenantId, modelId, revisionId, "udir/1",
            [.. inputs.Keys.Order(StringComparer.Ordinal)], [.. sources.Values.OrderBy(x => x.Id, StringComparer.Ordinal)], basis,
            [.. types.Values.OrderBy(x => x.Id, StringComparer.Ordinal)], [.. symbols], [.. expressions], [.. constraints],
            [.. objectives], [.. scenarios], [.. requires.Order(StringComparer.Ordinal)]));
        doc.ValidateMeasurements(measurements);
        // Enforce the existing bounded wire contract, not a new SDK-specific schema.
        _ = doc.Write();
        return frozen = new(doc, inputs.ToImmutableDictionary(StringComparer.Ordinal));
    }

    public static UdirDocument Import(ReadOnlyMemory<byte> bytes, MeasurementSemantics measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        var result = VersionedModelImport.Read(bytes);
        if (!result.Accepted) throw new ContractException(result.Diagnostic!.Code, "Model import rejected at " + result.Diagnostic.Stage + ".");
        var doc = result.Document!;
        doc.ValidateMeasurements(measurements);
        return doc;
    }
}
