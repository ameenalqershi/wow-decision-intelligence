using WowDi.Contracts;

namespace WowDi.Integration.Connectors;

/// <summary>Pure structural checks; does not grant authority, resolve secrets or commit checkpoints.</summary>
public static class ConnectorReadContract
{
    public static void ValidateRequest(ConnectorManifest manifest, ConnectorReadRequest request)
    {
        ConnectorManifestReader.Validate(manifest);
        ArgumentNullException.ThrowIfNull(request);
        var stream = request.Stream;
        Check(stream is not null && Text(stream.TenantId) && Text(stream.ConnectorInstanceId) &&
            Text(stream.StreamId) && stream.MappingVersion == manifest.MappingVersion,
            "connector.stream", "Invalid or incompatible stream identity.");
        Check(request.MaxRows > 0 && request.MaxRows <= manifest.Cursor.MaxBatchRows,
            "connector.batch", "Requested batch exceeds the declared limit.");
        Check(ValidPosition(manifest.Cursor.Mode, request.After), "connector.position", "Invalid cursor position.");
    }

    public static void ValidateBatch(ConnectorManifest manifest, ConnectorReadRequest request, ConnectorReadBatch batch)
    {
        ValidateRequest(manifest, request);
        ArgumentNullException.ThrowIfNull(batch);
        Check(!batch.Rows.IsDefault && batch.Rows.Length <= request.MaxRows, "connector.batch", "Invalid batch size.");
        Check(ValidPosition(manifest.Cursor.Mode, batch.Next), "connector.position", "Invalid cursor position.");
        foreach (var row in batch.Rows)
        {
            Check(row is not null, "connector.row", "Invalid source row.");
            Check(!row!.SourceKey.IsDefault && row.SourceKey.Length == manifest.Provenance.SourceKeyFields.Length &&
                row.SourceKey.All(Text) && row.SourceTimestamp != default &&
                row.SourceTimestamp.Offset == TimeSpan.Zero &&
                row.Values.ValueKind == System.Text.Json.JsonValueKind.Object,
                "connector.row", "Invalid row identity, UTC timestamp or source object.");
        }

        var mode = manifest.Cursor.Mode;
        if (mode == "snapshot")
        {
            Check(batch.Complete ? batch.Next is null : batch.Next is not null && batch.Next != request.After,
                "connector.position", "Incomplete snapshot must advance its staging continuation.");
            return;
        }
        // An empty final page may leave the checkpoint unchanged. Every other page must advance.
        var requiresProgress = !batch.Complete || batch.Rows.Length > 0;
        Check(!requiresProgress || batch.Next is not null, "connector.position", "Missing next checkpoint.");
        if (batch.Next is not null && request.After is not null)
        {
            Check(mode == "offset" ? batch.Next.Offset >= request.After.Offset : true,
                "connector.position", "Offset regressed.");
            Check(!requiresProgress || (mode == "offset" ? batch.Next.Offset > request.After.Offset : batch.Next != request.After),
                "connector.position", "Cursor did not advance.");
        }
    }

    private static bool ValidPosition(string mode, ConnectorPosition? value)
    {
        if (value is null) return true;
        return mode == "offset" ? value.OpaqueToken is null && value.Offset is >= 0
            : value.Offset is null && value.OpaqueToken is { Length: > 0 and <= 4096 } &&
              !value.OpaqueToken.Any(char.IsControl);
    }

    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        value == value.Trim() && !value.Any(char.IsControl);
    private static void Check(bool condition, string code, string message)
    {
        if (!condition) throw new ContractException(code, message);
    }
}
