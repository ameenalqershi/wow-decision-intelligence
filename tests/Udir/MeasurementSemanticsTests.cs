using System.Collections.Immutable;
using WowDi.Contracts;
using WowDi.Udir;
using Xunit;

namespace WowDi.Tests;

public sealed class MeasurementSemanticsTests
{
    private static readonly MeasurementSemantics Registry = new(
        [new("second", "time", "1"), new("minute", "time", "60"), new("hour", "time", "3600"),
         new("kg", "mass", "1"), new("g", "mass", "1/1000"), new("batch", "batch", "1")],
        ["USD", "EUR"], ["shift-a", "shift-b"]);
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;
    private static readonly FxSnapshot Quote = new("ledger/2026-01", "USD", "EUR", "5/6", Start, Start.AddDays(3));
    private static UdirDocument Model => UdirDocument.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "factory.json")));
    private static TimeBasis Basis => Model.Model.TimeBasis;
    private static void Reject(string code, Action action) => Assert.Equal("measure." + code, Assert.Throws<ContractException>(action).Code);

    [Fact]
    public void ModelAdmissionAcceptsFactoryRevenueAndRejectsUnconvertedRates()
    {
        Model.ValidateMeasurements(Registry);
        var m = Model.Model;
        foreach (var mismatch in new[] { "USD/hour", "EUR/minute", "USD/kg" })
        {
            var changed = m.Types.Select(t => t.Id == "price" ? t with { Unit = mismatch } : t).ToImmutableArray();
            var e = Assert.Throws<ContractException>(() => UdirDocument.Create(m with { Types = changed }).ValidateMeasurements(Registry));
            Assert.Equal("measure.expression", e.Code);
            Assert.Contains("return at input[189,9]", e.Message);
        }
    }

    [Fact]
    public void QuotientIsCheckedAgainstDeclaredRate()
    {
        var m = Model.Model;
        var ratio = new Expression("ratio", Operation.Divide, "price", ["return", "q"], m.Expressions[0].Span);
        UdirDocument.Create(m with { Expressions = m.Expressions.Add(ratio) }).ValidateMeasurements(Registry);
        Reject("expression", () => UdirDocument.Create(m with { Expressions = m.Expressions.Add(ratio with { Arguments = ["q", "return"] }) }).ValidateMeasurements(Registry));
    }

    [Theory]
    [InlineData("1", "g", "kg", "1/1000")]
    [InlineData("-3/4", "hour", "minute", "-45")]
    [InlineData("1/3", "USD/minute", "USD/hour", "20")]
    [InlineData("5", "batch*kg/batch", "g", "5000")]
    [InlineData("0", "g", "kg", "0")]
    public void ExplicitScaleIsExact(string input, string from, string to, string expected) =>
        Assert.Equal(expected, Registry.ConvertUnit(input, from, to));

    [Theory]
    [InlineData("USD", "EUR", "dimension")]
    [InlineData("kg", "minute", "dimension")]
    [InlineData("USD/minute", "USD", "dimension")]
    [InlineData("EUR/USD", "EUR/USD", "currency")]
    [InlineData("unknown", "g", "unit")]
    [InlineData("kg/", "g", "unit")]
    [InlineData("g//kg", "g", "unit")]
    public void InvalidConversionsAreRejected(string from, string to, string code) =>
        Reject(code, () => Registry.ConvertUnit("1", from, to));

    [Fact]
    public void CurrencyConversionCarriesVersionAndEffectiveTime()
    {
        var converted = Registry.ConvertCurrency("7/3", "USD", "EUR", Quote, Start);
        Assert.Equal(new FxConversion("35/18", "EUR", Quote, Start), converted);
        foreach (var invalid in new[] { Quote with { Version = "" }, Quote with { From = "EUR", To = "USD" },
            Quote with { Rate = "0" }, Quote with { Rate = "-1" }, Quote with { EffectiveUntil = Start } })
            Reject("fx", () => Registry.ConvertCurrency("1", "USD", "EUR", invalid, Start));
        Reject("fx", () => Registry.ConvertCurrency("1", "USD", "EUR", Quote, Start.AddTicks(-1)));
        Reject("fx", () => Registry.ConvertCurrency("1", "USD", "EUR", Quote, Quote.EffectiveUntil));
    }

    [Fact]
    public void CheckedNarrowingAndLargeExactResults()
    {
        Assert.Equal(long.MinValue, MeasurementSemantics.ToInt64Exact("-9223372036854775808"));
        Assert.Equal(long.MaxValue, MeasurementSemantics.ToInt64Exact("9223372036854775807"));
        Reject("fraction", () => MeasurementSemantics.ToInt64Exact("1/2"));
        Reject("overflow", () => MeasurementSemantics.ToInt64Exact("9223372036854775808"));
        Reject("overflow", () => MeasurementSemantics.ToInt64Exact("-9223372036854775809"));
        var large = new string('9', 256);
        Assert.Equal(large, Registry.ConvertUnit(large, "hour", "hour"));
        Reject("overflow", () => Registry.ConvertUnit(large, "hour", "second"));
        Reject("overflow", () => Registry.ConvertCurrency(large, "USD", "EUR", Quote with { Rate = "10" }, Start));
    }

    [Fact]
    public void ConvertingTicksRequiresSameCalendarZoneAndInstantOfOrigin()
    {
        Assert.Equal("120", Registry.ConvertTicks("2", Basis, Basis with { TickUnit = "second", Origin = "2026-01-01T01:00:00+01:00" }));
        foreach (var invalid in new[] { Basis with { CalendarId = "shift-b" }, Basis with { TimeZoneId = "America/New_York" },
            Basis with { Origin = "2026-01-02T00:00:00Z" } })
            Reject("basis", () => Registry.ConvertTicks("1", Basis, invalid));
        Reject("zone", () => Registry.ConvertTicks("1", Basis, Basis with { TimeZoneId = "Missing/Zone" }));
        Reject("origin", () => Registry.ConvertTicks("1", Basis, Basis with { Origin = "2026-01-01" }));
        Reject("tick", () => Registry.ConvertTicks("1", Basis, Basis with { TickUnit = "kg" }));
        Reject("calendar", () => Registry.ConvertTicks("1", Basis, Basis with { CalendarId = "missing" }));
    }

    [Fact]
    public void TemporalTypesShareAnElapsedTimeDimensionAndInstantsUseModelTicks()
    {
        var m = Model.Model;
        var instant = new ScalarType("instant", ScalarKind.Instant, "minute");
        UdirDocument.Create(m with { Types = m.Types.Add(instant) }).ValidateMeasurements(Registry);
        Reject("basis", () => UdirDocument.Create(m with { Types = m.Types.Add(instant with { Unit = "hour" }) }).ValidateMeasurements(Registry));
        Reject("tick", () => UdirDocument.Create(m with { Types = m.Types.Add(new("bad", ScalarKind.Duration, "kg")) }).ValidateMeasurements(Registry));
        Reject("currency", () => UdirDocument.Create(m with { Types = m.Types.Add(new("bad", ScalarKind.Money, Currency: "ZZZ")) }).ValidateMeasurements(Registry));
    }

    [Fact]
    public void LocalTimeRejectsGapAndRequiresExplicitOffsetInFold()
    {
        var basis = Basis with { TimeZoneId = "America/New_York" };
        var gap = new DateTime(2026, 3, 8, 2, 30, 0);
        var fold = new DateTime(2026, 11, 1, 1, 30, 0);
        Reject("localTime", () => Registry.ResolveLocal(basis, gap));
        Reject("offset", () => Registry.ResolveLocal(basis, fold));
        Reject("offset", () => Registry.ResolveLocal(basis, fold, TimeSpan.Zero));
        var first = Registry.ResolveLocal(basis, fold, TimeSpan.FromHours(-4));
        var second = Registry.ResolveLocal(basis, fold, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), first);
        Assert.Equal(TimeSpan.FromHours(1), second - first);
        Assert.Equal(second.AddHours(1), Registry.ResolveLocal(basis, fold.AddHours(1)));
    }

    [Theory]
    [InlineData("USD", "mass", "1")]
    [InlineData("a/b", "mass", "1")]
    [InlineData("test", "currency.USD", "1")]
    [InlineData("test", "mass", "0")]
    public void RegistryRejectsInvalidOrShadowedUnits(string symbol, string dimension, string scale) =>
        Reject("unit", () => new MeasurementSemantics([new(symbol, dimension, scale)], ["USD"], []));
}
