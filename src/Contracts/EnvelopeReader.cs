using System.Globalization;
using System.Text.Json;

namespace WowDi.Contracts;

/// <summary>A decoded envelope. The owner of the selected contract still validates its payload.</summary>
public sealed record ContractEnvelope(string Contract, ushort Major, ushort Minor,
    JsonElement Payload, JsonElement? Extensions);

/// <summary>A diagnostic code shared by HTTP, SDK, local imports and extension manifests.</summary>
public sealed class ContractException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class EnvelopeReader
{
    public const int MaximumEnvelopeBytes = 1024 * 1024;

    /// <summary>Validate envelope compatibility. Registered extension handlers validate their own values.</summary>
    public static ContractEnvelope Read(ReadOnlyMemory<byte> utf8, string expectedContract,
        ushort supportedMajor, IReadOnlyDictionary<string, Action<JsonElement>>? handlers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContract);
        if (supportedMajor == 0) throw new ArgumentOutOfRangeException(nameof(supportedMajor));
        if (utf8.Length > MaximumEnvelopeBytes)
            throw new ContractException("envelope.size", "The envelope exceeds the supported byte limit.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw new ContractException("envelope.json", "Expected bounded UTF-8 JSON with unique property names.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ContractException("envelope.shape", "The envelope must be an object.");

            foreach (var field in root.EnumerateObject())
                if (field.Name is not ("contract" or "version" or "payload" or "extensions"))
                    throw new ContractException("envelope.field", "Unrecognized envelope field.");

            var contract = ReadString(root, "contract");
            if (!string.Equals(contract, expectedContract, StringComparison.Ordinal))
                throw new ContractException("contract.identity", "Unexpected contract identity.");

            var version = ReadString(root, "version").Split('.');
            if (version.Length != 2 || !TryPart(version[0], out var major) || major == 0 || !TryPart(version[1], out var minor))
                throw new ContractException("contract.version", "Expected a canonical major.minor version.");
            if (major != supportedMajor)
                throw new ContractException("contract.major", "An explicit migration or a different reader is required.");
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                throw new ContractException("payload.shape", "This contract requires an object payload.");

            JsonElement? extensions = null;
            if (root.TryGetProperty("extensions", out var entries))
            {
                ValidateExtensions(entries, handlers);
                extensions = entries.Clone();
            }
            return new ContractEnvelope(contract, major, minor, payload.Clone(), extensions);
        }
    }

    private static void ValidateExtensions(JsonElement entries, IReadOnlyDictionary<string, Action<JsonElement>>? handlers)
    {
        if (entries.ValueKind != JsonValueKind.Object)
            throw new ContractException("extension.shape", "Expected an extension map.");
        foreach (var property in entries.EnumerateObject())
        {
            var ext = property.Value;
            if (property.Name.Length == 0 || ext.ValueKind != JsonValueKind.Object ||
                !ext.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !ext.TryGetProperty("value", out var value) || ext.EnumerateObject().Count() != 2)
                throw new ContractException("extension.shape", "Expected an extension ID, required flag and value.");

            // Always match wire IDs ordinally, even if a caller supplies a case-insensitive registry.
            var handler = handlers?.FirstOrDefault(pair => string.Equals(pair.Key, property.Name, StringComparison.Ordinal)).Value;
            if (handler != null) handler(value.Clone());
            else if (required.GetBoolean())
                throw new ContractException("extension.required", "The reader has no handler for a required extension.");
        }
    }

    private static bool TryPart(string value, out ushort result)
    {
        result = 0;
        return value.Length is > 0 and <= 5 && (value.Length == 1 || value[0] != '0')
            && value.All(char.IsAsciiDigit) && ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static string ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ContractException("envelope.shape", "Missing or invalid " + key + ".");
        return value.GetString()!;
    }
}
