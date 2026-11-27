using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Contracts;
using WowDi.Integration.Connectors;
using WowDi.ModelImport;
using WowDi.Udir;

namespace WowDi.Integration.Snapshots;

public sealed record TargetField(string Id,string Type,string? Unit,bool Required,bool AllowNull,
    bool Integral = false, bool NonNegative = false, bool Positive = false);
public sealed record TargetSchema(string Id,string Version,ImmutableArray<TargetField> Fields);
public sealed record NormalizedCell(string Type,string? Unit,string? Value);
public sealed record NormalizedRow(ImmutableArray<string> SourceKey,DateTimeOffset SourceTimestamp,
    string OriginalTimestamp,ImmutableSortedDictionary<string,NormalizedCell> Values);
public sealed record StagedSnapshot(ConnectorStreamIdentity Stream,string ManifestSha256,string OriginalSha256,
    string SemanticSha256,ImmutableArray<NormalizedRow> Rows);
public sealed record SnapshotResult(StagedSnapshot? Snapshot,ImmutableArray<RowDiagnostic> Diagnostics)
{
    public bool Accepted => Snapshot is not null && Diagnostics.IsEmpty;
}

/// <summary>Deterministic staging only. Host supplies authorized stream and immutable registered schema/manifest.</summary>
public static class SnapshotImport
{
    public static SnapshotResult Stage(ConnectorStreamIdentity stream, ReadOnlyMemory<byte> manifestBytes,
        TargetSchema target, ReadOnlyMemory<byte> source, SnapshotOptions options, CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if(source.Length>SnapshotParser.MaximumBytes || manifestBytes.Length>EnvelopeReader.MaximumEnvelopeBytes)
            return Failure("snapshot.size");
        ct.ThrowIfCancellationRequested();
        var manifestCopy=manifestBytes.ToArray();
        var original=source.ToArray();
        ConnectorManifest manifest;
        try
        {
            manifest=ConnectorManifestReader.Read(manifestCopy).Manifest;
            ConnectorReadContract.ValidateRequest(manifest,new(stream,null,manifest.Cursor.MaxBatchRows));
        }
        catch(ContractException ex) { return Failure(ex.Code); }
        if(manifest.Cursor.Mode!="snapshot") return Failure("snapshot.mode");
        if(!ValidTarget(manifest,target)) return Failure("snapshot.targetSchema");
        if(manifest.Fields.Any(f=>f.TransformId!="identity" || f.TransformVersion!="1.0"))
            return Failure("snapshot.unsupportedTransform");

        var targetFields=target.Fields.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        var diagnostics=ImmutableArray.CreateBuilder<RowDiagnostic>();
        var rows=new SortedDictionary<string,NormalizedRow>(StringComparer.Ordinal);
        long normalizedBudget=0;
        try
        {
            foreach(var row in SnapshotParser.Read(original,options,manifest.Cursor.MaxBatchRows,ct))
            {
                ct.ThrowIfCancellationRequested();
                if(diagnostics.Count>=1024) return Failure("snapshot.diagnosticLimit");
                normalizedBudget+=256L+manifest.Fields.Length*128L;
                if(normalizedBudget>8*1024*1024) return Failure("snapshot.normalizedSize");
                if(row.Error is { } structural) { diagnostics.Add(new(row.Number,null,structural)); continue; }
                var before=diagnostics.Count;
                void Error(string code,string? targetField=null)
                {
                    if(diagnostics.Count<1024) diagnostics.Add(new(row.Number,targetField,code));
                }
                var key=ImmutableArray.CreateBuilder<string>();
                foreach(var field in manifest.Provenance.SourceKeyFields)
                {
                    if(!row.Cells.TryGetValue(field,out var cell) || cell.Text is not {Length:>0 and <=256} value ||
                        value!=value.Trim() || value.Any(char.IsControl) || cell.Kind!=JsonValueKind.String)
                        Error("snapshot.sourceKey");
                    else key.Add(value);
                }
                var timestamp=default(DateTimeOffset);
                string originalTime="";
                if(!row.Cells.TryGetValue(manifest.Provenance.SourceTimestampField,out var stamp) ||
                    stamp.Kind!=JsonValueKind.String || stamp.Text is null ||
                    !TryTimestamp(stamp.Text,manifest,out timestamp))
                    Error("snapshot.timestamp");
                else originalTime=stamp.Text;

                var values=ImmutableSortedDictionary.CreateBuilder<string,NormalizedCell>(StringComparer.Ordinal);
                foreach(var mapping in manifest.Fields.OrderBy(x=>x.TargetField,StringComparer.Ordinal))
                {
                    if(!row.Cells.TryGetValue(mapping.SourceField,out var cell))
                    {
                        if(mapping.Required) Error("snapshot.required",mapping.TargetField);
                        continue;
                    }
                    if(cell.Kind==JsonValueKind.Null)
                    {
                        if(!mapping.AllowNull) Error("snapshot.null",mapping.TargetField);
                        else values.Add(mapping.TargetField,new(mapping.TargetType,mapping.TargetUnit,null));
                        continue;
                    }
                    if(!Normalize(cell,mapping.SourceType,manifest,options,out var value) ||
                        !CheckTargetValue(targetFields[mapping.TargetField],value))
                        Error("snapshot.value",mapping.TargetField);
                    else
                    {
                        normalizedBudget+=6L*(value.Length+mapping.TargetField.Length+(mapping.TargetUnit?.Length??0));
                        if(normalizedBudget>8*1024*1024) return Failure("snapshot.normalizedSize");
                        values.Add(mapping.TargetField,new(mapping.TargetType,mapping.TargetUnit,value));
                    }
                }
                if(diagnostics.Count!=before) continue;
                var immutableKey=key.ToImmutable();
                var encodedKey=JsonSerializer.Serialize(immutableKey);
                normalizedBudget+=6L*(encodedKey.Length+originalTime.Length);
                if(normalizedBudget>8*1024*1024) return Failure("snapshot.normalizedSize");
                if(!rows.TryAdd(encodedKey,new(immutableKey,timestamp,originalTime,values.ToImmutable())))
                    Error("snapshot.duplicateKey");
            }
        }
        catch(SnapshotException ex) { diagnostics.Add(new(ex.Row,null,ex.Code)); }
        catch(JsonException) { diagnostics.Add(new(0,null,"snapshot.json")); }
        if(diagnostics.Count>0) return new(null,diagnostics.ToImmutable());
        if(rows.Count==0) return Failure("snapshot.empty");
        var result=rows.Values.ToImmutableArray();
        var manifestHash=Hash(manifestCopy);
        // Canonical ordering and exact normalized representations. Raw source digest remains separate.
        var targetHash=Hash(JsonSerializer.SerializeToUtf8Bytes(new {target.Id,target.Version,fields=target.Fields.OrderBy(f=>f.Id,StringComparer.Ordinal)}));
        var semantic=JsonSerializer.SerializeToUtf8Bytes(new { stream,manifestHash,targetHash,rows=result });
        if(semantic.Length>8*1024*1024) return Failure("snapshot.normalizedSize");
        return new(new(stream,manifestHash,Hash(original),Hash(semantic),result),[]);
    }

    // M10 owns UDIR shape/domain validation. Never convert arbitrary rows into invented UDIR semantics.
    public static ModelImportResult ValidateGeneratedModel(ConnectorStreamIdentity stream,ReadOnlyMemory<byte> generatedUdir)
    {
        var result=VersionedModelImport.Read(generatedUdir);
        return result.Document is { } doc && doc.Model.TenantId!=stream.TenantId
            ? new(null,new(ModelImportStage.Domain,"snapshot.tenant")) : result;
    }

    private static bool ValidTarget(ConnectorManifest m,TargetSchema target)
    {
        if(m.TargetSchema!=target.Id || m.TargetSchemaVersion!=target.Version || target.Fields.IsDefaultOrEmpty ||
            target.Fields.Length>4096 || target.Fields.Any(x=>x is null || string.IsNullOrEmpty(x.Id) ||
                (x.Integral || x.NonNegative || x.Positive) && x.Type is not ("integer" or "decimal" or "duration" or "quantity" or "money" or "rate")) ||
            target.Fields.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=target.Fields.Length) return false;
        var fields=target.Fields.ToDictionary(x=>x.Id,StringComparer.Ordinal);
        return m.Fields.All(f=>fields.TryGetValue(f.TargetField,out var t) && f.TargetType==t.Type && f.TargetUnit==t.Unit &&
            f.Required==t.Required && f.AllowNull==t.AllowNull) &&
            target.Fields.Where(t=>t.Required).All(t=>m.Fields.Any(f=>f.TargetField==t.Id));
    }

    private static bool CheckTargetValue(TargetField field,string value) =>
        (!field.Integral || !value.Contains('.')) &&
        (!field.NonNegative || !value.StartsWith('-')) &&
        (!field.Positive || value!="0" && !value.StartsWith('-'));

    private static bool Normalize(Cell cell,string type,ConnectorManifest m,SnapshotOptions options,out string value)
    {
        value="";
        if(cell.Text is not { } raw || raw.Contains('\0')) return false;
        bool csv=options.Format==SnapshotFormat.Csv;
        switch(type)
        {
            case "text":
                if(cell.Kind!=JsonValueKind.String) return false;
                value=raw; return true;
            case "boolean":
                if(!csv && cell.Kind is not (JsonValueKind.True or JsonValueKind.False) || raw is not ("true" or "false")) return false;
                value=raw; return true;
            case "instant":
                if(cell.Kind!=JsonValueKind.String || !TryTimestamp(raw,m,out var utc)) return false;
                value=utc.ToString("O",CultureInfo.InvariantCulture); return true;
            case "integer": case "decimal": case "duration": case "quantity": case "money": case "rate":
                if(!csv && cell.Kind!=JsonValueKind.Number) return false;
                return ExactDecimal(raw,csv?options.DecimalSeparator:'.',type=="integer",out value);
            default: return false;
        }
    }

    // Exact lexical normalization, never floating point, no grouping, exponents, rounding or implicit currency.
    private static bool ExactDecimal(string raw,char separator,bool integer,out string value)
    {
        value="";
        if(raw.Length is <1 or >258) return false;
        bool negative=raw[0]=='-';
        var text=negative?raw[1..]:raw;
        var parts=text.Split(separator);
        if(parts.Length>2 || integer && parts.Length!=1 ||
            parts.Any(p=>p.Length==0 || !p.All(char.IsAsciiDigit)) || parts.Sum(p=>p.Length)>256) return false;
        var whole=parts[0].TrimStart('0'); if(whole.Length==0) whole="0";
        var fraction=parts.Length==2?parts[1].TrimEnd('0'):"";
        value=(negative && (whole!="0" || fraction.Length>0)?"-":"")+whole+(fraction.Length>0?"."+fraction:"");
        return true;
    }

    private static bool TryTimestamp(string raw,ConnectorManifest m,out DateTimeOffset value)
    {
        value=default;
        string[] utcFormats=["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        string[] offsetFormats=["yyyy-MM-dd'T'HH:mm:sszzz","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"];
        string[] localFormats=["yyyy-MM-dd'T'HH:mm:ss","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if(m.TimePolicy is "utc-only" or "explicit-offset")
        {
            if(DateTimeOffset.TryParseExact(raw,utcFormats,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var z))
            { value=z.ToUniversalTime(); return value!=default; }
            if(m.TimePolicy=="explicit-offset" &&
                DateTimeOffset.TryParseExact(raw,offsetFormats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var offset))
            { value=offset.ToUniversalTime(); return value!=default; }
            return false;
        }
        if(!DateTime.TryParseExact(raw,localFormats,CultureInfo.InvariantCulture,DateTimeStyles.None,out var local)) return false;
        var zone=TimeZoneInfo.FindSystemTimeZoneById(m.TimeZoneId!);
        if(zone.IsAmbiguousTime(local) || zone.IsInvalidTime(local)) return false;
        try { value=new DateTimeOffset(local,zone.GetUtcOffset(local)).ToUniversalTime(); return value!=default; }
        catch(ArgumentException) { return false; }
    }

    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static SnapshotResult Failure(string code)=>new(null,[new(0,null,code)]);
}
