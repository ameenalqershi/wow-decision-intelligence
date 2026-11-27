using System.Collections.Immutable;
using System.Globalization;
using WowDi.Contracts;

namespace WowDi.Udir;

internal static class ModelRules
{
    internal static void Check(Model m)
    {
        ArgumentNullException.ThrowIfNull(m);
        Need(m.SemanticsVersion == "udir/1", "semantics");
        Need(Text(m.TenantId) && Text(m.ModelId) && Text(m.RevisionId), "identity");
        Need(!m.InputDigests.IsDefaultOrEmpty && m.InputDigests.All(Hash) && Unique(m.InputDigests), "digest");
        Need(!m.Requires.IsDefault && m.Requires.All(Text) && Unique(m.Requires), "capability");
        var basis = m.TimeBasis;
        Need(basis != null && Text(basis.CalendarId) && Text(basis.TimeZoneId) && Text(basis.TickUnit)
             && ValidOrigin(basis.Origin), "time");
        var docs = Index(m.Sources, x => x.Id);
        foreach (var d in docs.Values) Need(Hash(d.Sha256) && d.Utf16Length >= 0, "source");
        void Span(SourceSpan span)
        {
            Need(span != null && Text(span.SourceId) && docs.TryGetValue(span.SourceId, out var doc)
                && span.Start >= 0 && span.Length >= 0 && span.Start <= doc.Utf16Length - span.Length, "span");
        }

        var types = Index(m.Types, x => x.Id);
        foreach (var type in types.Values)
        {
            Need(Enum.IsDefined(type.Kind), "type");
            var unitRequired = type.Kind is ScalarKind.Instant or ScalarKind.Duration or ScalarKind.Quantity or ScalarKind.Rate;
            bool unitOptional = type.Kind is ScalarKind.Integer or ScalarKind.Rational;
            Need(unitRequired ? Text(type.Unit) : type.Unit == null || unitOptional && Text(type.Unit), "unit");
            Need(type.Kind == ScalarKind.Money ? Text(type.Currency) : type.Currency == null, "currency");
        }
        var symbols = Index(m.Symbols, x => x.Id);
        foreach (var symbol in symbols.Values)
        {
            Span(symbol.Span);
            var type = Ref(types, symbol.TypeId);
            Need(Enum.IsDefined(symbol.Role), "symbol");
            if (type.Kind == ScalarKind.Boolean) Need(symbol.Lower == null && symbol.Upper == null, "domain");
            var lo = Bound(symbol.Lower, type.Kind);
            var hi = Bound(symbol.Upper, type.Kind);
            Need(lo == null || hi == null || lo.Value.CompareTo(hi.Value) <= 0, "domain");
        }

        var expressions = Index(m.Expressions, e => e.Id);
        foreach (var e in expressions.Values)
        {
            Span(e.Span);
            var result = Ref(types, e.TypeId);
            Need(Enum.IsDefined(e.Op) && !e.Arguments.IsDefault, "expression");
            int arity = e.Op switch { Operation.Literal or Operation.Symbol => 0, Operation.Negate => 1, _ => 2 };
            Need(e.Arguments.Length == arity, "arity");
            Need((e.Op == Operation.Literal) == (e.Literal != null) && (e.Op == Operation.Symbol) == (e.SymbolId != null), "expression");
            if (e.Op == Operation.Literal)
            {
                if (result.Kind == ScalarKind.Boolean) Need(e.Literal is "true" or "false", "literal");
                else _ = Bound(e.Literal, result.Kind);
                continue;
            }
            if (e.Op == Operation.Symbol)
            {
                Need(Ref(symbols, e.SymbolId!).TypeId == e.TypeId, "type");
                continue;
            }
            var argTypes = e.Arguments.Select(id => Ref(types, Ref(expressions, id).TypeId)).ToArray();
            if (e.Op is Operation.Equal or Operation.LessOrEqual or Operation.GreaterOrEqual)
                Need(result.Kind == ScalarKind.Boolean && argTypes[0].Id == argTypes[1].Id &&
                    (e.Op == Operation.Equal || argTypes[0].Kind != ScalarKind.Boolean), "type");
            else
            {
                Need(result.Kind != ScalarKind.Boolean && argTypes.All(t => t.Kind != ScalarKind.Boolean), "type");
                if (argTypes.Any(t => t.Kind == ScalarKind.Instant) || result.Kind == ScalarKind.Instant)
                {
                    // The tick unit is explicit; calendar-aware conversions belong to semantic admission.
                    var left = argTypes[0];
                    var right = argTypes.Length == 2 ? argTypes[1] : null;
                    bool difference = e.Op == Operation.Subtract && left.Kind == ScalarKind.Instant &&
                        right?.Id == left.Id && result.Kind == ScalarKind.Duration && result.Unit == left.Unit;
                    bool shift = e.Op is Operation.Add or Operation.Subtract && left.Kind == ScalarKind.Instant &&
                        right?.Kind == ScalarKind.Duration && right.Unit == left.Unit && result.Id == left.Id;
                    Need(difference || shift, "type");
                }
                else if (e.Op is Operation.Add or Operation.Subtract or Operation.Negate)
                    Need(argTypes.All(t => t.Id == e.TypeId), "type");
            }
        }
        CheckAcyclic(expressions);
        var constraints = Index(m.Constraints, c => c.Id);
        foreach (var c in constraints.Values)
        {
            Span(c.Span);
            Need(Ref(types, Ref(expressions, c.PredicateId).TypeId).Kind == ScalarKind.Boolean, "predicate");
            Need(c.Hard ? c.Penalty == null : c.Penalty != null && ExactNumber.Parse(c.Penalty).Numerator.Sign > 0, "penalty");
        }
        var objectives = Index(m.Objectives, o => o.Id);
        var priorities = new HashSet<int>();
        foreach (var o in objectives.Values)
        {
            Span(o.Span);
            Need(Ref(types, Ref(expressions, o.ExpressionId).TypeId).Kind != ScalarKind.Boolean, "objective");
            Need(Enum.IsDefined(o.Direction) && o.Priority > 0 && priorities.Add(o.Priority) &&
                ExactNumber.Parse(o.Weight).Numerator.Sign > 0 && ExactNumber.Parse(o.Tolerance).Numerator.Sign >= 0, "objective");
        }
        var scenarios = Index(m.Scenarios, s => s.Id);
        Need(scenarios.Count == 0 || scenarios.Values.Count(s => s.ParentId == null) == 1, "scenario");
        foreach (var s in scenarios.Values)
        {
            Span(s.Span);
            Need(Text(s.InformationSet) && (s.ParentId == null ? s.Stage == 0 : s.Stage > Ref(scenarios, s.ParentId).Stage), "scenario");
            var overrides = Index(s.Overrides, item => item.SymbolId);
            foreach (var item in overrides.Values)
            {
                var symbol = Ref(symbols, item.SymbolId);
                Need(symbol.Role != SymbolRole.Variable && Text(item.Value), "override");
                var kind = Ref(types, symbol.TypeId).Kind;
                if (kind == ScalarKind.Boolean) Need(item.Value is "true" or "false", "override");
                else
                {
                    var n = Bound(item.Value, kind)!.Value;
                    Need((symbol.Lower == null || n.CompareTo(ExactNumber.Parse(symbol.Lower)) >= 0) &&
                         (symbol.Upper == null || n.CompareTo(ExactNumber.Parse(symbol.Upper)) <= 0), "override");
                }
            }
            if (s.Probability != null)
            {
                var p = ExactNumber.Parse(s.Probability);
                Need(p.Numerator.Sign >= 0 && p.Numerator <= p.Denominator &&
                    (s.ParentId != null || p.Numerator == p.Denominator), "probability");
            }
        }
    }

    private static void CheckAcyclic(Dictionary<string, Expression> nodes)
    {
        var pending = nodes.Values.ToDictionary(e => e.Id, e => e.Arguments.Length);
        var parents = nodes.Keys.ToDictionary(id => id, _ => new List<string>());
        foreach (var e in nodes.Values)
            foreach (var input in e.Arguments) parents[input].Add(e.Id);
        var ready = new Queue<string>(pending.Keys.Where(id => pending[id] == 0));
        int seen = 0;
        while (ready.TryDequeue(out var id))
        {
            seen++;
            foreach (var parent in parents[id]) if (--pending[parent] == 0) ready.Enqueue(parent);
        }
        Need(seen == nodes.Count, "cycle");
    }

    private static bool ValidOrigin(string? value) =>
        value != null && (value.EndsWith('Z') || value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':') &&
        DateTimeOffset.TryParseExact(value, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static ExactNumber? Bound(string? raw, ScalarKind kind)
    {
        if (raw is null) return null;
        var n = ExactNumber.Parse(raw);
        if (kind is ScalarKind.Integer or ScalarKind.Instant) Need(n.Denominator.IsOne, "domain");
        return n;
    }

    private static Dictionary<string, T> Index<T>(ImmutableArray<T> entries, Func<T, string> key) where T : class
    {
        Need(!entries.IsDefault, "collection");
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var entry in entries) Need(entry != null && Text(key(entry)) && result.TryAdd(key(entry), entry), "id");
        return result;
    }

    private static T Ref<T>(Dictionary<string, T> entries, string id) => Text(id) && entries.TryGetValue(id, out var entry)
        ? entry : throw new ContractException("udir.reference", "A UDIR reference could not be resolved.");

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Unique(ImmutableArray<string> values) => values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    private static void Need(bool condition, string code)
    {
        if (!condition) throw new ContractException("udir." + code, "UDIR validation failed: " + code + ".");
    }
}
