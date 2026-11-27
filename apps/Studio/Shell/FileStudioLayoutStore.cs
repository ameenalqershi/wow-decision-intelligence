using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowDi.Studio.Shell;

// Host supplies a trusted per-user presentation path. Only this bounded
// layout document is written, never model content, tokens or credentials.
public sealed class FileStudioLayoutStore(string path) : IStudioLayoutStore
{
    private const int MaxBytes = 1024 * 1024;
    private readonly string file = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter<InspectorDock>(allowIntegerValues: false) }
    };
    public async ValueTask<LayoutLoad> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaxBytes) return new LayoutLoad(null, true);
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.ReadByte() != -1) return new LayoutLoad(null, true);
            // Reject duplicate JSON members; last-wins parsing is unsafe for
            // version/context identifiers.
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (!UniqueMembers(doc.RootElement)) return new LayoutLoad(null, true);
            var layout = JsonSerializer.Deserialize<ShellLayout>(bytes, Options);
            return new LayoutLoad(layout, layout is null);
        }
        catch (FileNotFoundException) { return new LayoutLoad(null); }
        catch (DirectoryNotFoundException) { return new LayoutLoad(null); }
        catch (JsonException) { return new LayoutLoad(null, true); }
        catch (EndOfStreamException) { return new LayoutLoad(null, true); }
    }

    public async ValueTask SaveAsync(ShellLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(layout, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Layout exceeds the storage bound.");
        var directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(file) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return value.EnumerateObject().All(p => names.Add(p.Name) && UniqueMembers(p.Value));
        }
        return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(UniqueMembers);
    }
}
