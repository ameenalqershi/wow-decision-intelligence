using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using WowDi.Udir;

namespace WowDi.ModelingSdk;

public sealed class FrozenInput
{
    public string Digest { get; }
    public ImmutableArray<byte> Bytes { get; }
    public FrozenInput(ReadOnlyMemory<byte> bytes)
    {
        var owned = bytes.ToArray();
        Digest = Convert.ToHexStringLower(SHA256.HashData(owned));
        Bytes = ImmutableArray.CreateRange(owned);
    }
}

public sealed class ModelSource
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string text;
    public FrozenInput Input { get; }
    public SourceDocument Document { get; }
    public ModelSource(string id, ReadOnlyMemory<byte> utf8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Input = new(utf8);
        text = StrictUtf8.GetString(Input.Bytes.AsSpan());
        Document = new(id, Input.Digest, text.Length);
    }
    public SourceSpan Span(int start, int length)
    {
        if (start < 0 || length < 0 || start > text.Length - length)
            throw new ArgumentOutOfRangeException(nameof(start));
        return new(Document.Id, start, length);
    }
    // Locate an unambiguous fragment in the supplied original bytes; never invent a source.
    public SourceSpan Locate(string fragment)
    {
        ArgumentException.ThrowIfNullOrEmpty(fragment);
        var index = text.IndexOf(fragment, StringComparison.Ordinal);
        if (index < 0 || text.IndexOf(fragment, index + 1, StringComparison.Ordinal) >= 0)
            throw new ArgumentException("Source fragment is missing or ambiguous.", nameof(fragment));
        return Span(index, fragment.Length);
    }
}

public sealed record BuiltModel(UdirDocument Document, ImmutableDictionary<string, ImmutableArray<byte>> Inputs);
