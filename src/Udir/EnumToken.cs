using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowDi.Udir;

// The wire profile accepts only exact declared names, including for future enums.
// Numeric aliases, case folding and combined names would hide invalid input.
internal sealed class EnumToken<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> Tokens =
        Enum.GetValues<T>().ToDictionary(v => v.ToString(), StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Tokens.TryGetValue(reader.GetString()!, out var value))
            return value;
        throw new JsonException("Unknown UDIR enumeration token.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Enum.GetName(value) ?? throw new JsonException("Unknown UDIR enumeration value."));
}
