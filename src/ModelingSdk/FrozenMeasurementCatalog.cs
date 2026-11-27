using System.Text.Json;
using WowDi.Udir;

namespace WowDi.ModelingSdk;

/// <summary>Frozen declared measurement inputs. System time-zone rules must be pinned by the host separately.</summary>
public sealed class FrozenMeasurementCatalog
{
    public MeasurementSemantics Semantics { get; }
    public FrozenInput Input { get; }

    public FrozenMeasurementCatalog(IEnumerable<UnitDefinition> units, IEnumerable<string> currencies, IEnumerable<string> calendars)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(currencies);
        ArgumentNullException.ThrowIfNull(calendars);
        var definitions = units.OrderBy(x => x.Symbol, StringComparer.Ordinal).ToArray();
        var codes = currencies.Order(StringComparer.Ordinal).ToArray();
        var calendarIds = calendars.Order(StringComparer.Ordinal).ToArray();
        Semantics = new(definitions, codes, calendarIds);
        // Private evidence snapshot, not a new public UDIR wire family.
        Input = new(JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "wowdi.sdk.measurement-catalog/1",
            units = definitions.Select(x => new { symbol = x.Symbol, dimension = x.Dimension, scale = x.Scale }),
            currencies = codes,
            calendars = calendarIds
        }));
    }
}
