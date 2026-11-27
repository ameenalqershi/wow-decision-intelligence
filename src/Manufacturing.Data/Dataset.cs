using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace WowDi.Manufacturing.Data;

// V01 source profile, not a solver model, UDIR envelope or REST DTO.
// All times are elapsed integer minutes from the explicit UTC origin.
public sealed record FactoryClock(
    [property: JsonRequired] string CalendarId,
    [property: JsonRequired] string TimeZoneId,
    [property: JsonRequired] string Origin,
    [property: JsonRequired] string TickUnit,
    [property: JsonRequired] int HorizonMinutes);
public sealed record Downtime([property: JsonRequired] int StartMinute, [property: JsonRequired] int EndMinute);
public sealed record MachineCalendar([property: JsonRequired] string MachineId,
    [property: JsonRequired] ImmutableArray<Downtime> Downtime);
public sealed record MaterialStock([property: JsonRequired] string MaterialId, [property: JsonRequired] string Unit,
    [property: JsonRequired] string InitialQuantity);
public sealed record Receipt([property: JsonRequired] string ReceiptId, [property: JsonRequired] string MaterialId,
    [property: JsonRequired] string Unit, [property: JsonRequired] string Quantity, [property: JsonRequired] int AvailableMinute);
public sealed record Consumption([property: JsonRequired] string MaterialId, [property: JsonRequired] string Unit,
    [property: JsonRequired] string Quantity);
public sealed record FactoryOperation(
    [property: JsonRequired] string OperationId, [property: JsonRequired] string JobId,
    [property: JsonRequired] string MachineId, [property: JsonRequired] int ReleaseMinute,
    [property: JsonRequired] int DurationMinutes, [property: JsonRequired] int DueMinute,
    [property: JsonRequired] string TardinessWeight,
    [property: JsonRequired] ImmutableArray<Consumption> Materials,
    int? HardDeadlineMinute = null);
public sealed record Precedence([property: JsonRequired] string Predecessor, [property: JsonRequired] string Successor);
public sealed record FactoryDataset(
    [property: JsonRequired] string Profile, [property: JsonRequired] string RevisionId,
    [property: JsonRequired] FactoryClock Clock, [property: JsonRequired] string ObjectiveUnit,
    [property: JsonRequired] ImmutableArray<MachineCalendar> Calendars,
    [property: JsonRequired] ImmutableArray<MaterialStock> Materials,
    [property: JsonRequired] ImmutableArray<Receipt> Receipts,
    [property: JsonRequired] ImmutableArray<FactoryOperation> Operations,
    [property: JsonRequired] ImmutableArray<Precedence> Precedence,
    string? Currency = null);

public sealed record DatasetDiagnostic(string Code, string Path, string Message);
public sealed class DatasetException(DatasetDiagnostic diagnostic) : Exception(diagnostic.Code + " at " + diagnostic.Path + ": " + diagnostic.Message)
{
    public DatasetDiagnostic Diagnostic { get; } = diagnostic;
}

// An admitted source snapshot retains the exact source bytes and their
// SHA-256 identity. Registry publication and authorization remain M11/RT01.
public sealed class FrozenFactoryDataset
{
    internal FrozenFactoryDataset(FactoryDataset dataset, ImmutableArray<byte> bytes, string digest)
    { Dataset = dataset; SourceBytes = bytes; Sha256 = digest; }
    public FactoryDataset Dataset { get; }
    public ImmutableArray<byte> SourceBytes { get; }
    public string Sha256 { get; }
}
