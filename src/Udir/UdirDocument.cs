using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowDi.Contracts;

namespace WowDi.Udir;

public sealed class UdirDocument
{
    private const string ContractId = "wowdi.udir";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new EnumToken<ScalarKind>(), new EnumToken<SymbolRole>(), new EnumToken<Operation>(), new EnumToken<Direction>() }
    };

    public Model Model { get; }
    public ushort MinorVersion { get; }
    public JsonElement? Extensions { get; }

    private UdirDocument(Model model, ushort minor, JsonElement? extensions)
    {
        ModelRules.Check(model);
        Model = model;
        MinorVersion = minor;
        Extensions = extensions;
    }

    /// <summary>Check the structurally validated model against the host measurement registry.</summary>
    public void ValidateMeasurements(MeasurementSemantics measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        measurements.Validate(Model);
    }

    public static UdirDocument Create(Model model) => new(model, 0, null);

    public static UdirDocument Read(ReadOnlyMemory<byte> bytes)
    {
        var envelope = EnvelopeReader.Read(bytes, ContractId, 1);
        try
        {
            var model = envelope.Payload.Deserialize<Model>(Options) ?? throw new JsonException();
            return new UdirDocument(model, envelope.Minor, envelope.Extensions);
        }
        catch (JsonException)
        {
            throw new ContractException("udir.payload", "Invalid typed UDIR payload.");
        }
    }

    public byte[] Write()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", ContractId);
            writer.WriteString("version", FormattableString.Invariant($"1.{MinorVersion}"));
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, Model, Options);
            if (Extensions is { } extensions)
            {
                writer.WritePropertyName("extensions");
                extensions.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > EnvelopeReader.MaximumEnvelopeBytes)
            throw new ContractException("envelope.size", "The encoded model exceeds the supported envelope size.");
        return buffer.WrittenSpan.ToArray();
    }
}
