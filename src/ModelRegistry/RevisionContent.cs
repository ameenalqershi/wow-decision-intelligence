using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.DecisionGraph;
using WowDi.Udir;

namespace WowDi.ModelRegistry;

public sealed record StoredRevision(UdirDocument Document, DecisionGraphSnapshot Graph,
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
            throw RevisionContent.Error("graph");
        return DecisionGraphSnapshot.Create(key, data.Entities, data.Relationships);
    }
}

internal static class RevisionContent
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static ContractException Error(string code) => new("registry." + code, "Invalid or conflicting registry " + code + ".");
    internal static GraphRevision Key(UdirDocument doc) => new(doc.Model.TenantId, doc.Model.ModelId, doc.Model.RevisionId);

    internal static void Validate(UdirDocument doc, Dictionary<string, byte[]> inputs)
    {
        var expected = doc.Model.InputDigests.Concat(doc.Model.Sources.Select(s => s.Sha256)).ToHashSet(StringComparer.Ordinal);
        if (!expected.SetEquals(inputs.Keys)) throw Error("inputs");
        foreach (var (digest, bytes) in inputs)
            if (Hash(bytes) != digest) throw Error("digest");
        foreach (var source in doc.Model.Sources)
        {
            int length;
            try { length = StrictUtf8.GetCharCount(inputs[source.Sha256]); }
            catch (DecoderFallbackException) { throw Error("encoding"); }
            if (length != source.Utf16Length) throw Error("sourceLength");
        }
    }
}
