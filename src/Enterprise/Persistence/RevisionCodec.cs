using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.Udir;

namespace WowDi.Enterprise.Persistence;

public sealed record EnterpriseRevision(UdirDocument Document, DecisionGraphSnapshot Graph,
    ImmutableDictionary<string, ImmutableArray<byte>> Inputs);

internal sealed record GraphData(GraphRevision Revision, GraphEntity[] Entities, RelationshipInput[] Relationships)
{
    internal static byte[] Encode(DecisionGraphSnapshot graph) =>
        JsonSerializer.SerializeToUtf8Bytes(new GraphData(graph.Revision, [.. graph.Entities.Values],
            [.. graph.Relationships.Values.Select(e => new RelationshipInput(e.Id, e.Kind, e.OwnerId,
                e.Targets, e.Source, e.Window, e.Capacity))]));
    internal static DecisionGraphSnapshot Decode(byte[] bytes, GraphRevision key)
    {
        var data = JsonSerializer.Deserialize<GraphData>(bytes);
        if (data is null || data.Revision != key || data.Entities is null || data.Relationships is null)
            throw RevisionCodec.Error("graph");
        return DecisionGraphSnapshot.Create(key, data.Entities, data.Relationships);
    }
}

internal static class RevisionCodec
{
    internal static ContractException Error(string code) => new("registry." + code, "Invalid or conflicting registry " + code + ".");
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static GraphRevision Key(UdirDocument doc) => new(doc.Model.TenantId, doc.Model.ModelId, doc.Model.RevisionId);
    internal static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) || value != value.Trim())
            throw Error("key");
    }
    internal static void Validate(UdirDocument doc, Dictionary<string, byte[]> inputs)
    {
        var expected = doc.Model.InputDigests.Concat(doc.Model.Sources.Select(s => s.Sha256)).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(inputs.Keys)) throw Error("inputs");
        foreach (var (digest, bytes) in inputs)
            if (Hash(bytes) != digest) throw Error("digest");
        var utf8 = new UTF8Encoding(false, true);
        foreach (var source in doc.Model.Sources)
        {
            int length;
            try { length = utf8.GetCharCount(inputs[source.Sha256]); }
            catch (DecoderFallbackException) { throw Error("encoding"); }
            if (length != source.Utf16Length) throw Error("sourceLength");
        }
    }
}
