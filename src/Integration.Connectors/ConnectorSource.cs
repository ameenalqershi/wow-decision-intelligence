using System.Collections.Immutable;
using System.Text.Json;

namespace WowDi.Integration.Connectors;

// Tenant/instance/schema/mapping/stream identity must be resolved by the integration host.
// Adapter output is untrusted; it cannot choose tenant or publish a model revision.
public sealed record ConnectorStreamIdentity(string TenantId, string ConnectorInstanceId, string MappingVersion, string StreamId);

// Cursor is opaque to the host in opaque mode; an offset is nonnegative and partition-specific.
// Snapshot mode uses an ephemeral opaque staging token only for incomplete pages; it is not a durable checkpoint.
public sealed record ConnectorPosition(string? OpaqueToken, long? Offset);

public sealed record ConnectorReadRequest(ConnectorStreamIdentity Stream, ConnectorPosition? After, int MaxRows);

public sealed record ConnectorSourceRow(ImmutableArray<string> SourceKey, DateTimeOffset SourceTimestamp, JsonElement Values);

public sealed record ConnectorReadBatch(ImmutableArray<ConnectorSourceRow> Rows, ConnectorPosition? Next, bool Complete);

// Read only. Credentials are resolved by the authorized connector host in its isolated scope, never in the request.
// I02/I03 implement source validation and I04 owns durable atomic checkpoint publication and reconciliation.
public interface IConnectorSource
{
    ValueTask<ConnectorReadBatch> ReadAsync(ConnectorReadRequest request, CancellationToken cancellationToken);
}
