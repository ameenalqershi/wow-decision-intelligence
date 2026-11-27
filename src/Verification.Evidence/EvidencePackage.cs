using System.Buffers.Binary;
using WowDi.ModelRegistry;

namespace WowDi.Verification.Evidence;

/// <summary>
/// A portable, bounded binary container of exact evidence artifacts. No compression, extraction,
/// filesystem paths or executable invocation. The manifest remains an F05 versioned envelope.
/// OriginalSha256 covers the container; EvidenceId covers the manifest, and they are distinct.
/// </summary>
public static class EvidencePackage
{
    private static ReadOnlySpan<byte> Magic=>"WDIE0001"u8;
    public const int MaximumPackageBytes=8*1024*1024;
    private static readonly int[] Limits=[EvidenceRecord.MaximumArtifactBytes,262144,
        EvidenceRecord.MaximumArtifactBytes,EvidenceRecord.MaximumArtifactBytes,EvidenceRecord.MaximumArtifactBytes,65536];

    public static byte[] Export(EvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var parts=new[]{record.ManifestBytes,record.CatalogBytes,record.CapabilitiesBytes,record.SolverResultBytes,record.AssessmentBytes,record.SettingsBytes};
        int size=Magic.Length+4*parts.Length;
        for(int i=0;i<parts.Length;i++)
        {
            if(parts[i].IsDefaultOrEmpty||parts[i].Length>Limits[i])throw EvidenceRecord.Error("packageSize");
            size=checked(size+parts[i].Length);
        }
        if(size>MaximumPackageBytes)throw EvidenceRecord.Error("packageSize");
        var bytes=new byte[size];Magic.CopyTo(bytes);
        int offset=Magic.Length;
        foreach(var part in parts)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset,4),part.Length);offset+=4;
            part.AsSpan().CopyTo(bytes.AsSpan(offset));offset+=part.Length;
        }
        return bytes;
    }

    /// <summary>Expected hashes and tenant come from the authorized durable record, never from an untrusted import.</summary>
    public static EvidenceRecord Replay(StoredRevision revision,string trustedTenant,string expectedEvidenceId,string expectedOriginalSha256,
        ReadOnlyMemory<byte> package,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(package.Length<Magic.Length+4*Limits.Length||package.Length>MaximumPackageBytes)
            throw EvidenceRecord.Error("packageSize");
        var bytes=package.ToArray();
        if(!EvidenceRecord.Digest(expectedOriginalSha256)||EvidenceRecord.Hash(bytes)!=expectedOriginalSha256)
            throw EvidenceRecord.Error("packageDigest");
        if(!bytes.AsSpan(0,Magic.Length).SequenceEqual(Magic))throw EvidenceRecord.Error("packageVersion");
        var parts=new ReadOnlyMemory<byte>[Limits.Length];
        int offset=Magic.Length;
        for(int i=0;i<parts.Length;i++)
        {
            ct.ThrowIfCancellationRequested();
            if(bytes.Length-offset<4)throw EvidenceRecord.Error("packageFraming");
            var length=BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset,4));offset+=4;
            if(length<1||length>Limits[i]||length>bytes.Length-offset)throw EvidenceRecord.Error("packageFraming");
            parts[i]=bytes.AsMemory(offset,length);offset+=length;
        }
        if(offset!=bytes.Length)throw EvidenceRecord.Error("packageFraming");
        return EvidenceRecord.Replay(revision,trustedTenant,expectedEvidenceId,parts[0],parts[1],parts[2],parts[3],parts[4],parts[5],ct);
    }
}
