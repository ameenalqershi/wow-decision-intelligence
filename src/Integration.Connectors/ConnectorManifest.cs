using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowDi.Contracts;

namespace WowDi.Integration.Connectors;

// The connector contract is independent of the engine and ERP vendors.
// These are mapping declarations, not executable expressions or authority to activate a connector.
public sealed record ConnectorManifest
{
    public required string ConnectorKind { get; init; }
    public required string SourceSchema { get; init; }
    public required string SourceSchemaVersion { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetSchemaVersion { get; init; }
    public required string MappingVersion { get; init; }
    public required ImmutableArray<FieldMapping> Fields { get; init; }
    public required ProvenancePolicy Provenance { get; init; }
    public required CursorPolicy Cursor { get; init; }
    public required RetryPolicy Retry { get; init; }
    public required string Reconciliation { get; init; }
    public required string TimePolicy { get; init; }
    public required string? TimeZoneId { get; init; }
    public required string CurrencyPolicy { get; init; }
    public required string? CredentialReference { get; init; }
}

public sealed record FieldMapping
{
    public required string SourceField { get; init; }
    public required string TargetField { get; init; }
    public required string SourceType { get; init; }
    public required string TargetType { get; init; }
    public required string? SourceUnit { get; init; }
    public required string? TargetUnit { get; init; }
    public required bool Required { get; init; }
    public required bool AllowNull { get; init; }
    public required string TransformId { get; init; }
    public required string TransformVersion { get; init; }
}

public sealed record ProvenancePolicy
{
    public required ImmutableArray<string> SourceKeyFields { get; init; }
    public required string SourceTimestampField { get; init; }
}

public sealed record CursorPolicy
{
    public required string Mode { get; init; }
    public required int MaxBatchRows { get; init; }
}

public sealed record RetryPolicy
{
    public required int MaxAttempts { get; init; }
    public required int InitialDelayMs { get; init; }
    public required int MaxDelayMs { get; init; }
}

public sealed record DecodedConnectorManifest(ConnectorManifest Manifest, ushort WireMinor, JsonElement? Extensions);

public static class ConnectorManifestReader
{
    public const string ContractId = "wowdi.integration.connector-manifest";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public static DecodedConnectorManifest Read(ReadOnlyMemory<byte> utf8)
    {
        var envelope = EnvelopeReader.Read(utf8, ContractId, 1);
        ConnectorManifest manifest;
        try
        {
            manifest = envelope.Payload.Deserialize<ConnectorManifest>(JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ContractException("connector.shape", "Invalid connector manifest payload.");
        }
        Validate(manifest);
        return new(manifest, envelope.Minor, envelope.Extensions);
    }

    // Also validate a locally constructed manifest before admission. Never trust construction alone.
    public static void Validate(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Check(Id(manifest.ConnectorKind) && Text(manifest.SourceSchema) && Text(manifest.SourceSchemaVersion) &&
            Text(manifest.TargetSchema) && Text(manifest.TargetSchemaVersion) && Version(manifest.MappingVersion),
            "connector.identity", "Invalid connector or mapping identity.");
        Check(!manifest.Fields.IsDefaultOrEmpty && manifest.Fields.Length <= 4096,
            "connector.fields", "A bounded nonempty mapping is required.");
        Check(manifest.Provenance is not null && !manifest.Provenance.SourceKeyFields.IsDefaultOrEmpty &&
            manifest.Provenance.SourceKeyFields.Length <= 32 &&
            manifest.Provenance.SourceKeyFields.All(Text) &&
            manifest.Provenance.SourceKeyFields.Distinct(StringComparer.Ordinal).Count() == manifest.Provenance.SourceKeyFields.Length &&
            Text(manifest.Provenance.SourceTimestampField), "connector.provenance", "Source keys and timestamp field are required.");

        Check(manifest.Cursor is not null && manifest.Cursor.Mode is "snapshot" or "opaque" or "offset" &&
            manifest.Cursor.MaxBatchRows is >= 1 and <= 10000, "connector.cursor", "Invalid cursor policy.");
        Check(manifest.Retry is not null && manifest.Retry.MaxAttempts is >= 1 and <= 10 &&
            manifest.Retry.InitialDelayMs is >= 1 and <= 3600000 &&
            manifest.Retry.MaxDelayMs >= manifest.Retry.InitialDelayMs && manifest.Retry.MaxDelayMs <= 3600000,
            "connector.retry", "Invalid bounded retry policy.");
        Check(manifest.Reconciliation is "none" or "idempotency-and-lookup", "connector.reconciliation",
            "Unknown reconciliation capability.");
        Check(manifest.CurrencyPolicy is "none" or "explicit-currency" or "rate-snapshot",
            "connector.currency", "Unknown currency policy.");
        Check(manifest.TimePolicy is "utc-only" or "explicit-offset" or "named-zone",
            "connector.time", "Unknown timestamp policy.");
        Check(manifest.TimePolicy == "named-zone"
            ? Text(manifest.TimeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(manifest.TimeZoneId!, out _)
            : manifest.TimeZoneId is null, "connector.time", "Invalid timezone declaration.");
        Check(manifest.CredentialReference is null ||
            (manifest.CredentialReference.StartsWith("cred:", StringComparison.Ordinal) &&
                Id(manifest.CredentialReference[5..])),
            "connector.credentials", "A credential reference identifier, not a secret, is required.");

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in manifest.Fields)
        {
            Check(field is not null, "connector.fields", "Null field mapping.");
            ValidateField(field!, manifest, targets);
        }
    }

    private static void ValidateField(FieldMapping field, ConnectorManifest manifest, HashSet<string> targets)
    {
        Check(Text(field.SourceField) && Id(field.TargetField) && targets.Add(field.TargetField),
            "connector.fields", "Invalid or duplicate target field.");
        Check(Type(field.SourceType) && Type(field.TargetType), "connector.type", "Unknown mapping field type.");
        Check(Unit(field.SourceType, field.SourceUnit) && Unit(field.TargetType, field.TargetUnit),
            "connector.unit", "Missing or incompatible unit declaration.");
        Check(Id(field.TransformId) && Version(field.TransformVersion),
            "connector.transform", "A versioned transform identifier is required.");
        Check(field.TransformId != "identity" ||
            (field.TransformVersion == "1.0" && field.SourceType == field.TargetType && field.SourceUnit == field.TargetUnit),
            "connector.transform", "Identity cannot change type or unit.");
        Check(field.SourceType != "money" && field.TargetType != "money" || manifest.CurrencyPolicy != "none",
            "connector.currency", "Money requires an explicit currency policy.");
        Check(field.SourceType != "money" || field.TargetType != "money" || field.SourceUnit == field.TargetUnit ||
            manifest.CurrencyPolicy == "rate-snapshot", "connector.currency", "Currency conversion requires an exchange-rate snapshot.");
    }

    private static bool Type(string? value) => value is "boolean" or "integer" or "decimal" or "text" or "instant"
        or "duration" or "quantity" or "money" or "rate";
    private static bool Unit(string type, string? unit) => type is "duration" or "quantity" or "money" or "rate"
        ? Text(unit) : unit is null;
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value == value.Trim() && !value.Any(char.IsControl);
    private static bool Id(string? value) => value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    private static bool Version(string? value)
    {
        if (value is null) return false;
        var parts = value.Split('.');
        return parts.Length == 2 && Part(parts[0], out var major) && major > 0 && Part(parts[1], out _);
    }
    private static bool Part(string part, out ushort result)
    {
        result = 0;
        return part.Length is > 0 and <= 5 && (part.Length == 1 || part[0] != '0') &&
            part.All(char.IsAsciiDigit) && ushort.TryParse(part, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out result);
    }
    private static void Check(bool condition, string code, string diagnostic)
    {
        if (!condition) throw new ContractException(code, diagnostic);
    }
}
