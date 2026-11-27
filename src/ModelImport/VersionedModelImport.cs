using System.Text.Json;
using Json.Schema;
using WowDi.Contracts;
using WowDi.Udir;

namespace WowDi.ModelImport;

public enum ModelImportStage { Compatibility, Schema, Domain }

public sealed record ModelImportDiagnostic(ModelImportStage Stage, string Code);
public sealed record ModelImportResult(UdirDocument? Document, ModelImportDiagnostic? Diagnostic)
{
    public bool Accepted => Document is not null && Diagnostic is null;
}

/// <summary>Import boundary for wire UDIR v1; no registry mutation, solver or implicit remote fetch.</summary>
public static class VersionedModelImport
{
    private static readonly Lazy<string> SchemaText = new(() =>
    {
        using var stream = typeof(VersionedModelImport).Assembly.GetManifestResourceStream("WowDi.ModelImport.Schemas.udir-v1.schema.json")
            ?? throw new InvalidOperationException("Embedded model schema missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(SchemaText.Value, new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

    public static string GetSchemaV1() => SchemaText.Value;

    /// <summary>F05 compatibility followed by JSON Schema only; does not validate references or mathematics.</summary>
    public static ModelImportDiagnostic? ValidateSchema(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > EnvelopeReader.MaximumEnvelopeBytes)
            return new(ModelImportStage.Compatibility, "envelope.size");
        // Snapshot mutable caller memory before crossing validation stages.
        return ValidateOwned(utf8.ToArray());
    }

    public static ModelImportResult Read(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length > EnvelopeReader.MaximumEnvelopeBytes)
            return new(null, new(ModelImportStage.Compatibility, "envelope.size"));
        var owned = utf8.ToArray();
        if (ValidateOwned(owned) is { } error) return new(null, error);
        try { return new(UdirDocument.Read(owned), null); }
        catch (ContractException ex) { return new(null, new(ModelImportStage.Domain, ex.Code)); }
    }

    private static ModelImportDiagnostic? ValidateOwned(byte[] utf8)
    {
        try { _ = EnvelopeReader.Read(utf8, "wowdi.udir", 1); }
        catch (ContractException ex) { return new(ModelImportStage.Compatibility, ex.Code); }

        using var json = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        // Trusted embedded schema only: all $ref values point to its own $defs; instance data never selects a schema.
        var result = Schema.Value.Evaluate(json.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        return result.IsValid ? null : new(ModelImportStage.Schema, "modelImport.schema");
    }
}
