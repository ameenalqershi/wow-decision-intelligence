using System.Collections.Immutable;
using System.IO.Compression;
using System.IO.Hashing;

namespace WowDi.Enterprise.DataSecurity;

public sealed class DataSecurityException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ArchiveLimits(int CompressedBytes = 8 * 1024 * 1024, int ExpandedBytes = 16 * 1024 * 1024,
    int EntryBytes = 4 * 1024 * 1024, int Entries = 128);

public sealed record ArchiveMember(string Name, ImmutableArray<byte> Bytes);

/// <summary>Reads into bounded staging memory. Never extracts a path or returns partially validated contents.</summary>
public static class BoundedArchive
{
    public static async Task<ImmutableArray<ArchiveMember>> ReadAsync(Stream input, ArchiveLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.CompressedBytes is < 1 or > 32 * 1024 * 1024 ||
            limits.ExpandedBytes is < 1 or > 64 * 1024 * 1024 ||
            limits.EntryBytes < 1 || limits.EntryBytes > limits.ExpandedBytes ||
            limits.Entries is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        try
        {
            using var compressed = new MemoryStream();
            await CopyBoundedAsync(input, compressed, limits.CompressedBytes, cancellationToken);
            compressed.Position = 0;
            using var archive = new ZipArchive(compressed, ZipArchiveMode.Read);
            if (archive.Entries.Count == 0 || archive.Entries.Count > limits.Entries)
                throw new DataSecurityException("archive.entries");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = ImmutableArray.CreateBuilder<ArchiveMember>(archive.Entries.Count);
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                var dosFlags = entry.ExternalAttributes & 0xffff;
                if (!SafeName(entry.FullName) || unixType is not (0 or 0x8000) ||
                    (dosFlags & 0x410) != 0 ||
                    !names.Add(entry.FullName) ||
                    names.Any(n => !string.Equals(n, entry.FullName, StringComparison.OrdinalIgnoreCase) &&
                        (n.StartsWith(entry.FullName + "/", StringComparison.OrdinalIgnoreCase) ||
                         entry.FullName.StartsWith(n + "/", StringComparison.OrdinalIgnoreCase))))
                    throw new DataSecurityException("archive.path");
                if (entry.Length < 0 || entry.Length > limits.EntryBytes || entry.Length > limits.ExpandedBytes - expanded)
                    throw new DataSecurityException("archive.size");
                using var member = entry.Open();
                using var buffer = new MemoryStream();
                await CopyBoundedAsync(member, buffer, Math.Min(limits.EntryBytes, limits.ExpandedBytes - expanded), cancellationToken);
                if (buffer.Length != entry.Length ||
                    Crc32.HashToUInt32(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))) != entry.Crc32)
                    throw new DataSecurityException("archive.invalid");
                expanded += buffer.Length;
                result.Add(new(entry.FullName, ImmutableArray.CreateRange(buffer.ToArray())));
            }
            return result.MoveToImmutable();
        }
        catch (InvalidDataException)
        {
            throw new DataSecurityException("archive.invalid");
        }
    }

    private static async Task CopyBoundedAsync(Stream source, MemoryStream target, long limit, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = await source.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (count > limit - target.Length) throw new DataSecurityException("archive.size");
            target.Write(buffer, 0, count);
        }
    }

    private static bool SafeName(string name)
    {
        if (name.Length is < 1 or > 240) return false;
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length is < 1 or > 100 || segment is "." or ".." || segment.EndsWith('.') ||
                !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return false;
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3]))) return false;
        }
        return true;
    }
}
