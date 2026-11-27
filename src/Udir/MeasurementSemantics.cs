using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using WowDi.Contracts;

namespace WowDi.Udir;

public sealed record UnitDefinition(string Symbol, string Dimension, string Scale);
public sealed record FxSnapshot(string Version, string From, string To, string Rate, DateTimeOffset EffectiveFrom, DateTimeOffset EffectiveUntil);
public sealed record FxConversion(string Amount, string Currency, FxSnapshot Snapshot, DateTimeOffset AsOf);

/// <summary>Immutable measurement registry shared by import, semantic admission and SDK callers.</summary>
public sealed class MeasurementSemantics
{
    private readonly Dictionary<string, Measure> units = new(StringComparer.Ordinal);
    private readonly HashSet<string> currencies = new(StringComparer.Ordinal);
    private readonly HashSet<string> calendars;
    private static readonly ExactNumber One = new(BigInteger.One, BigInteger.One);
    private sealed record Measure(ImmutableDictionary<string, int> Powers, ExactNumber Scale);
    private static readonly Measure Scalar = new(ImmutableDictionary<string, int>.Empty, One);

    public MeasurementSemantics(IEnumerable<UnitDefinition> definitions, IEnumerable<string> currencies,
        IEnumerable<string> calendars)
    {
        this.calendars = new(calendars, StringComparer.Ordinal);
        if (this.calendars.Any(string.IsNullOrWhiteSpace)) throw Error("calendar");
        foreach (var code in currencies)
        {
            if (code is not { Length: 3 } || !code.All(char.IsAsciiLetterUpper) || !this.currencies.Add(code)) throw Error("currency");
            units.Add(code, new(Scalar.Powers.Add("currency." + code, 1), One));
        }
        foreach (var definition in definitions)
        {
            if (!Identifier(definition.Symbol) || !Identifier(definition.Dimension)) throw Error("unit");
            var scale = ExactNumber.Parse(definition.Scale);
            if (scale.Numerator.Sign <= 0 || !units.TryAdd(definition.Symbol, new(Scalar.Powers.Add(definition.Dimension, 1), scale)))
                throw Error("unit");
        }
    }

    private static bool Identifier(string? token) => token is { Length: > 0 and <= 64 } && char.IsAsciiLetter(token[0])
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static ContractException Error(string code, string? context = null) =>
        new("measure." + code, $"Measurement validation failed ({code}) {context}".TrimEnd());

    private static string Format(ExactNumber n) => n.Denominator.IsOne
        ? n.Numerator.ToString(CultureInfo.InvariantCulture)
        : FormattableString.Invariant($"{n.Numerator}/{n.Denominator}");

    private static ExactNumber Reduce(BigInteger numerator, BigInteger denominator)
    {
        var gcd = BigInteger.GreatestCommonDivisor(numerator, denominator);
        var result = new ExactNumber(numerator / gcd, denominator / gcd);
        if (BigInteger.Abs(result.Numerator).ToString(CultureInfo.InvariantCulture).Length > 256 ||
            result.Denominator.ToString(CultureInfo.InvariantCulture).Length > 256) throw Error("overflow");
        return result;
    }

    private static Measure Combine(Measure left, Measure right, bool divide)
    {
        var powers = left.Powers.ToBuilder();
        foreach (var (dimension, exponent) in right.Powers)
        {
            var next = powers.GetValueOrDefault(dimension) + (divide ? -exponent : exponent);
            if (next == 0) powers.Remove(dimension);
            else powers[dimension] = next;
        }
        var a = left.Scale;
        var b = right.Scale;
        return new(powers.ToImmutable(), Reduce(a.Numerator * (divide ? b.Denominator : b.Numerator),
            a.Denominator * (divide ? b.Numerator : b.Denominator)));
    }

    private Measure ParseUnit(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 256) throw Error("unit");
        var value = Scalar;
        var start = 0;
        var divide = false;
        string? currency = null;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('*' or '/')) continue;
            var token = text[start..i];
            if (!units.TryGetValue(token, out var atom)) throw Error("unit", token);
            if (currencies.Contains(token))
            {
                if (currency != null && currency != token) throw Error("currency", text);
                currency = token;
            }
            value = Combine(value, atom, divide);
            divide = i < text.Length && text[i] == '/';
            start = i + 1;
        }
        return value;
    }

    private static bool SameDimensions(Measure a, Measure b) => a.Powers.Count == b.Powers.Count &&
        a.Powers.All(p => b.Powers.TryGetValue(p.Key, out int n) && n == p.Value);
    private static bool Elapsed(Measure m) => m.Powers.Count == 1 && m.Powers.GetValueOrDefault("time") == 1;

    public string ConvertUnit(string amount, string from, string to)
    {
        var a = ParseUnit(from);
        var b = ParseUnit(to);
        if (!SameDimensions(a, b)) throw Error("dimension");
        var n = ExactNumber.Parse(amount);
        return Format(Reduce(n.Numerator * a.Scale.Numerator * b.Scale.Denominator,
            n.Denominator * a.Scale.Denominator * b.Scale.Numerator));
    }

    public static long ToInt64Exact(string amount)
    {
        var n = ExactNumber.Parse(amount);
        if (!n.Denominator.IsOne) throw Error("fraction");
        if (n.Numerator < long.MinValue || n.Numerator > long.MaxValue) throw Error("overflow");
        return (long)n.Numerator;
    }

    public FxConversion ConvertCurrency(string amount, string from, string to, FxSnapshot snapshot, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!currencies.Contains(from) || !currencies.Contains(to) || from == to ||
            snapshot.From != from || snapshot.To != to || string.IsNullOrWhiteSpace(snapshot.Version) ||
            asOf < snapshot.EffectiveFrom || asOf >= snapshot.EffectiveUntil) throw Error("fx");
        var rate = ExactNumber.Parse(snapshot.Rate);
        if (rate.Numerator.Sign <= 0) throw Error("fx");
        var n = ExactNumber.Parse(amount);
        return new(Format(Reduce(n.Numerator * rate.Numerator, n.Denominator * rate.Denominator)), to, snapshot, asOf);
    }

    private (TimeZoneInfo Zone, DateTimeOffset Origin) Basis(TimeBasis basis)
    {
        ArgumentNullException.ThrowIfNull(basis);
        if (!calendars.Contains(basis.CalendarId)) throw Error("calendar", basis.CalendarId);
        if (!Elapsed(ParseUnit(basis.TickUnit))) throw Error("tick");
        if (!ModelRules.ValidOrigin(basis.Origin)) throw Error("origin");
        if (string.IsNullOrWhiteSpace(basis.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(basis.TimeZoneId, out var zone))
            throw Error("zone", basis.TimeZoneId);
        return (zone, DateTimeOffset.Parse(basis.Origin, CultureInfo.InvariantCulture));
    }

    public string ConvertTicks(string ticks, TimeBasis from, TimeBasis to)
    {
        var source = Basis(from);
        var target = Basis(to);
        if (from.CalendarId != to.CalendarId || from.TimeZoneId != to.TimeZoneId || source.Origin != target.Origin) throw Error("basis");
        return ConvertUnit(ticks, from.TickUnit, to.TickUnit);
    }

    public DateTimeOffset ResolveLocal(TimeBasis basis, DateTime local, TimeSpan? offset = null)
    {
        var zone = Basis(basis).Zone;
        if (local.Kind != DateTimeKind.Unspecified || zone.IsInvalidTime(local)) throw Error("localTime");
        var offsets = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local) : [zone.GetUtcOffset(local)];
        if (offset is null && offsets.Length != 1 || offset.HasValue && !offsets.Contains(offset.Value)) throw Error("offset");
        return new DateTimeOffset(local, offset ?? offsets[0]).ToUniversalTime();
    }

    internal void Validate(Model model)
    {
        _ = Basis(model.TimeBasis);
        var tick = ParseUnit(model.TimeBasis.TickUnit);
        var dimensions = new Dictionary<string, Measure>(StringComparer.Ordinal);
        foreach (var type in model.Types)
        {
            if (type.Kind == ScalarKind.Money && !currencies.Contains(type.Currency!)) throw Error("currency", type.Id);
            var symbol = type.Currency ?? type.Unit;
            var unit = symbol == null ? Scalar : ParseUnit(symbol);
            if (type.Kind is ScalarKind.Instant or ScalarKind.Duration && !Elapsed(unit)) throw Error("tick", type.Id);
            if (type.Kind == ScalarKind.Instant && tick.Scale != unit.Scale) throw Error("basis", type.Id);
            dimensions.Add(type.Id, unit);
        }
        var expressions = model.Expressions.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var expr in model.Expressions.Where(e => e.Op is Operation.Multiply or Operation.Divide))
        {
            var left = dimensions[expressions[expr.Arguments[0]].TypeId];
            var right = dimensions[expressions[expr.Arguments[1]].TypeId];
            var expected = Combine(left, right, expr.Op == Operation.Divide);
            var actual = dimensions[expr.TypeId];
            if (!SameDimensions(expected, actual) || expected.Scale != actual.Scale)
                throw Error("expression", $"{expr.Id} at {expr.Span.SourceId}[{expr.Span.Start},{expr.Span.Length}]");
        }
    }
}
