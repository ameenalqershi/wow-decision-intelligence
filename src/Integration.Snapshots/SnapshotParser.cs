using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace WowDi.Integration.Snapshots;

public enum SnapshotFormat { Csv, Json }
public sealed record SnapshotOptions(SnapshotFormat Format, char Delimiter = ',', char DecimalSeparator = '.');
public sealed record RowDiagnostic(int Row, string? TargetField, string Code);
internal sealed record Cell(string? Text, JsonValueKind Kind);
internal sealed record RawRow(int Number, Dictionary<string, Cell> Cells, string? Error = null);
internal sealed class SnapshotException(string code, int row = 0) : Exception(code)
{
    internal string Code { get; } = code;
    internal int Row { get; } = row;
}

internal static class SnapshotParser
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumCell = 8192, MaximumFields = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Decode(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf})) return Utf8.GetString(bytes,3,bytes.Length-3);
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException) { throw new SnapshotException("snapshot.encoding"); }
    }

    internal static IEnumerable<RawRow> Read(byte[] bytes, SnapshotOptions options, int maxRows, CancellationToken ct)
    {
        if (bytes.Length > MaximumBytes) throw new SnapshotException("snapshot.size");
        if (options.Format == SnapshotFormat.Csv && options.Delimiter is ',' or ';' or '\t' &&
            options.DecimalSeparator is '.' or ',' && options.Delimiter != options.DecimalSeparator)
            return Csv(Decode(bytes), options.Delimiter, maxRows, ct);
        if (options.Format == SnapshotFormat.Json && options.Delimiter == ',' && options.DecimalSeparator == '.')
            return Json(Decode(bytes), maxRows, ct);
        throw new SnapshotException("snapshot.options");
    }

    private static IEnumerable<RawRow> Json(string text, int maxRows, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(text,new JsonDocumentOptions { MaxDepth=16, AllowDuplicateProperties=false });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new SnapshotException("snapshot.shape");
        if (doc.RootElement.GetArrayLength() > maxRows) throw new SnapshotException("snapshot.rows");
        var number = 0;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (row.ValueKind != JsonValueKind.Object) { yield return new(number,new(StringComparer.Ordinal),"snapshot.rowShape"); continue; }
            var cells = new Dictionary<string,Cell>(StringComparer.Ordinal);
            foreach (var field in row.EnumerateObject())
            {
                if (cells.Count >= MaximumFields || !ValidHeader(field.Name)) throw new SnapshotException("snapshot.field",number);
                var value = field.Value;
                string? raw = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Null => null,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                    _ => throw new SnapshotException("snapshot.cellShape",number)
                };
                if (raw?.Length > MaximumCell) throw new SnapshotException("snapshot.cellSize",number);
                if (!cells.TryAdd(field.Name,new(raw,value.ValueKind))) throw new SnapshotException("snapshot.duplicateField",number);
            }
            yield return new(number,cells);
        }
    }

    private static IEnumerable<RawRow> Csv(string text,char delimiter,int maxRows,CancellationToken ct)
    {
        using var records = CsvRecords(text,delimiter,ct).GetEnumerator();
        if (!records.MoveNext()) throw new SnapshotException("snapshot.header");
        var header = records.Current.Select(x=>x.Text!).ToArray();
        if (header.Length == 0 || header.Any(x=>!ValidHeader(x)) ||
            header.Distinct(StringComparer.Ordinal).Count()!=header.Length) throw new SnapshotException("snapshot.header");
        var number=0;
        while (records.MoveNext())
        {
            number++;
            if (number>maxRows) throw new SnapshotException("snapshot.rows");
            var record=records.Current;
            if (record.Count!=header.Length) { yield return new(number,new(StringComparer.Ordinal),"snapshot.columnCount"); continue; }
            var cells = new Dictionary<string,Cell>(StringComparer.Ordinal);
            for (var i=0;i<header.Length;i++) cells.Add(header[i],record[i]);
            yield return new(number,cells);
        }
    }

    // RFC 4180-style quoting, LF or CRLF record separators. Bare CR and malformed quotes fail.
    private static IEnumerable<List<Cell>> CsvRecords(string text,char delimiter,CancellationToken ct)
    {
        var index=0; var recordNumber=0;
        while(index<text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var record=new List<Cell>();
            while(true)
            {
                var quoted=index<text.Length && text[index]=='"';
                var sb=new StringBuilder();
                if(quoted)
                {
                    index++; var closed=false;
                    while(index<text.Length)
                    {
                        if((index&4095)==0) ct.ThrowIfCancellationRequested();
                        var c=text[index++];
                        if(c=='"')
                        {
                            if(index<text.Length && text[index]=='"') { index++; sb.Append('"'); }
                            else { closed=true; break; }
                        }
                        else sb.Append(c);
                        if(sb.Length>MaximumCell) throw new SnapshotException("snapshot.cellSize",recordNumber);
                    }
                    if(!closed) throw new SnapshotException("snapshot.csvSyntax",recordNumber);
                }
                else
                {
                    while(index<text.Length && text[index]!=delimiter && text[index] is not ('\r' or '\n'))
                    {
                        var c=text[index++];
                        if(c=='"') throw new SnapshotException("snapshot.csvSyntax",recordNumber);
                        sb.Append(c);
                        if(sb.Length>MaximumCell) throw new SnapshotException("snapshot.cellSize",recordNumber);
                    }
                }
                if(record.Count>=MaximumFields) throw new SnapshotException("snapshot.fields",recordNumber);
                var value=sb.ToString();
                record.Add(!quoted && recordNumber>0 && value=="\\N" ? new(null,JsonValueKind.Null) : new(value,JsonValueKind.String));
                if(index==text.Length) break;
                if(text[index]==delimiter) { index++; continue; }
                if(text[index]=='\r')
                {
                    if(index+1>=text.Length || text[index+1]!='\n') throw new SnapshotException("snapshot.csvSyntax",recordNumber);
                    index+=2; break;
                }
                if(text[index]=='\n') { index++; break; }
                throw new SnapshotException("snapshot.csvSyntax",recordNumber);
            }
            yield return record;
            recordNumber++;
        }
    }

    private static bool ValidHeader(string? value) => value is {Length:>0 and <=256} &&
        value==value.Trim() && !value.Any(char.IsControl);
}
