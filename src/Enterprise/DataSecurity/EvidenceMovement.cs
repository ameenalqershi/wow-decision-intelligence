using System.Security.Cryptography;
using System.Text.Json;
using WowDi.Enterprise.Identity;

namespace WowDi.Enterprise.DataSecurity;

public sealed record EvidenceKey(string TenantId, string ModelId, string RevisionId, string EvidenceId);
public enum EvidenceMathStatus { Optimal, Feasible, Infeasible, Unbounded, Unknown }

// Internal repository record: original bytes are never copied into the redacted export.
public sealed record StoredEvidence(EvidenceKey Key, ReadOnlyMemory<byte> OriginalBytes, string OriginalSha256,
    EvidenceMathStatus Status, DateTimeOffset ExpiresAt, bool LegalHold, int ReferenceCount);

public sealed record MovementAudit(EvidenceKey Key, string Issuer, string Subject, string Action, DateTimeOffset Timestamp);
public sealed record RedactedEvidence(ReadOnlyMemory<byte> Bytes, string Sha256);
public enum RetentionResult { Missing, NotExpired, Protected, Removed }

// Store contract: retention must atomically re-read expiry, hold and references under the full key,
// remove only evidence bytes, and preserve audit + immutable tombstone. Export must record audit
// before returning bytes. Implementations must not expose unscoped enumeration.
public interface IEvidenceMovementStore
{
    ValueTask<StoredEvidence?> ReadAsync(EvidenceKey key, CancellationToken cancellationToken);
    // Atomically confirm the immutable digest and unexpired current record, then append audit.
    ValueTask<bool> CommitExportAuditAsync(EvidenceKey key, string originalSha256, MovementAudit audit, CancellationToken cancellationToken);
    ValueTask<RetentionResult> ExpireAsync(EvidenceKey key, MovementAudit audit, CancellationToken cancellationToken);
}

public static class EvidenceRetentionPolicy
{
    public static RetentionResult Evaluate(StoredEvidence evidence, DateTimeOffset now) =>
        evidence.ExpiresAt > now ? RetentionResult.NotExpired :
        evidence.LegalHold || evidence.ReferenceCount != 0 ? RetentionResult.Protected : RetentionResult.Removed;
}

public sealed class EvidenceMovement(DataMovementGate access, IEvidenceMovementStore store, TimeProvider clock)
{
    public async ValueTask<RedactedEvidence?> ExportAsync(EvidenceKey requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var actor = await access.AuthorizeAsync(new(requested.TenantId, requested.ModelId), DataMovementAction.RedactedExport, cancellationToken);
        if (actor is null) return null;
        ValidateKey(requested);
        var key = requested with { TenantId = actor.TenantId };
        var evidence = await store.ReadAsync(key, cancellationToken);
        if (evidence is null || evidence.Key != key) return null;
        var now = clock.GetUtcNow();
        if (evidence.ExpiresAt <= now) return null;
        if (evidence.OriginalBytes.Length > 16 * 1024 * 1024 || !Enum.IsDefined(evidence.Status))
            throw new DataSecurityException("evidence.invalid");
        var digest = Convert.ToHexStringLower(SHA256.HashData(evidence.OriginalBytes.Span));
        if (!string.Equals(digest, evidence.OriginalSha256, StringComparison.Ordinal))
            throw new DataSecurityException("evidence.digest");
        // Fixed projection: no arbitrary metadata, source data, token, connector reference or free text.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            revisionId = key.RevisionId,
            evidenceId = key.EvidenceId,
            originalSha256 = digest,
            mathematicalStatus = evidence.Status.ToString(),
            redacted = true
        });
        if (!await store.CommitExportAuditAsync(key, digest, new(key, actor.Issuer, actor.Subject, "RedactedExport", clock.GetUtcNow()), cancellationToken)) return null;
        return new(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    // This is an authorized model-scoped retention request, not an unrestricted background sweep.
    // The durable store owns the atomic recheck; legal hold and reference integrity override expiry.
    public async ValueTask<RetentionResult> ExpireAsync(EvidenceKey requested, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var actor = await access.AuthorizeAsync(new(requested.TenantId, requested.ModelId), DataMovementAction.Retention, cancellationToken);
        if (actor is null) return RetentionResult.Missing;
        ValidateKey(requested);
        var key = requested with { TenantId = actor.TenantId };
        return await store.ExpireAsync(key, new(key, actor.Issuer, actor.Subject, "RetentionExpired", clock.GetUtcNow()), cancellationToken);
    }

    private static void ValidateKey(EvidenceKey key)
    {
        if (!Valid(key.TenantId) || !Valid(key.ModelId) || !Valid(key.RevisionId) || !Valid(key.EvidenceId))
            throw new DataSecurityException("evidence.key");
    }
    private static bool Valid(string value) => value is { Length: > 0 and <= 256 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
