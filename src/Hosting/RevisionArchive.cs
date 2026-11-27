using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.ModelRegistry;
using WowDi.Udir;

namespace WowDi.Hosting;

// This is a frozen-input archive, not a solver verification certificate.
public sealed record RevisionArchive(string Digest, ImmutableArray<byte> Bytes);

public static class LocalRevisionTransfer
{
    private const string Contract = "wowdi.local.revision";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record InputBlob(string Digest, byte[] Bytes);
    private sealed record Payload(byte[] Model, GraphEntity[] Entities, RelationshipInput[] Relationships, InputBlob[] Inputs);

    public static RevisionArchive Export(LocalWorkspace workspace, string modelId, string revisionId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var stored = workspace.FindRevision(modelId, revisionId) ?? throw LocalWorkspace.Error("revisionMissing");
        var payload = new Payload(stored.Document.Write(), [.. stored.Graph.Entities.Values],
            [.. stored.Graph.Relationships.Values.Select(e => new RelationshipInput(e.Id, e.Kind, e.OwnerId, e.Targets, e.Source, e.Window, e.Capacity))],
            [.. stored.Inputs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new InputBlob(p.Key, p.Value.ToArray()))]);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { contract = Contract, version = "1.0", payload }, Options);
        if (bytes.Length > EnvelopeReader.MaximumEnvelopeBytes) throw LocalWorkspace.Error("archiveSize");
        return new(Digest(bytes), ImmutableArray.CreateRange(bytes));
    }

    public static GraphRevision Import(LocalWorkspace workspace, RevisionArchive archive)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Bytes.IsDefault || archive.Bytes.Length > EnvelopeReader.MaximumEnvelopeBytes) throw LocalWorkspace.Error("archiveSize");
        var bytes = archive.Bytes.ToArray();
        if (!string.Equals(archive.Digest, Digest(bytes), StringComparison.Ordinal)) throw LocalWorkspace.Error("archiveDigest");
        var envelope = EnvelopeReader.Read(bytes, Contract, 1);
        if (envelope.Minor != 0 || envelope.Extensions is not null) throw LocalWorkspace.Error("archiveVersion");
        Payload payload;
        try
        {
            payload = envelope.Payload.Deserialize<Payload>(Options) ?? throw new JsonException();
            if (payload.Model is null || payload.Entities is null || payload.Relationships is null || payload.Inputs is null)
                throw new JsonException();
        }
        catch (JsonException) { throw LocalWorkspace.Error("archivePayload"); }
        var model = UdirDocument.Read(payload.Model);
        var key = new GraphRevision(model.Model.TenantId, model.Model.ModelId, model.Model.RevisionId);
        if (!string.Equals(key.TenantId, workspace.Identity.TenantId, StringComparison.Ordinal))
            throw LocalWorkspace.Error("scope");
        var graph = DecisionGraphSnapshot.Create(key, payload.Entities, payload.Relationships);
        var inputs = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var input in payload.Inputs)
            if (input is null || string.IsNullOrWhiteSpace(input.Digest) || input.Bytes is null || !inputs.TryAdd(input.Digest, input.Bytes))
                throw LocalWorkspace.Error("archiveInputs");
        // Registry validates source hashes and identity, then commits revision+inputs in one transaction.
        return workspace.SaveRevision(model, graph, inputs);
    }

    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
