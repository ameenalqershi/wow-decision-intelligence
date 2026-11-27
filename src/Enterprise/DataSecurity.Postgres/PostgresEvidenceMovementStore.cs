using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace WowDi.Enterprise.DataSecurity.Postgres;

/// <summary>
/// Trusted infrastructure port. Registration/reference/hold methods are not public HTTP operations.
/// The authoritative S10 producer must supply previously verified immutable evidence; this store does not verify mathematics.
/// </summary>
public sealed class PostgresEvidenceMovementStore(DbContextOptions<SecurityDb> options):IEvidenceMovementStore
{
    public async ValueTask RegisterPublishedAsync(StoredEvidence evidence,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Validate(evidence.Key);
        if(evidence.OriginalBytes.Length is <1 or >16*1024*1024 || evidence.ReferenceCount!=0 ||
            !Enum.IsDefined(evidence.Status)) throw new DataSecurityException("evidence.invalid");
        var bytes=evidence.OriginalBytes.ToArray();
        var digest=Convert.ToHexStringLower(SHA256.HashData(bytes));
        if(digest!=evidence.OriginalSha256) throw new DataSecurityException("evidence.digest");
        var key=evidence.Key;
        var expires=evidence.ExpiresAt.ToUniversalTime();
        // PostgreSQL timestamp precision is microseconds. Normalize once for exact retries.
        expires=new DateTimeOffset(expires.Ticks-expires.Ticks%10,TimeSpan.Zero);
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var added=await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wowdi_security.artifacts (tenant,model_id,revision_id,evidence_id,bytes,digest,status,expires_at,legal_hold,purged_at)
            VALUES ({key.TenantId},{key.ModelId},{key.RevisionId},{key.EvidenceId},{bytes},{digest},{evidence.Status.ToString()},{expires},{evidence.LegalHold},NULL)
            ON CONFLICT (tenant,model_id,revision_id,evidence_id) DO NOTHING
            """,ct);
        if(added==0)
        {
            var row=await LockedAsync(db,key,ct)??throw new DataSecurityException("evidence.missing");
            // Tombstones are never resurrected. Holds are mutable only through the dedicated audited operation.
            if(row.Bytes is null || row.Digest!=digest || !row.Bytes.AsSpan().SequenceEqual(bytes) ||
                row.Status!=evidence.Status.ToString() || row.ExpiresAt!=expires)
                throw new DataSecurityException("evidence.conflict");
        }
        await tx.CommitAsync(ct);
    }

    public async ValueTask<StoredEvidence?> ReadAsync(EvidenceKey key,CancellationToken ct)
    {
        Validate(key);
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        var row=await db.Artifacts.AsNoTracking().SingleOrDefaultAsync(x=>x.Tenant==key.TenantId&&x.ModelId==key.ModelId&&
            x.RevisionId==key.RevisionId&&x.EvidenceId==key.EvidenceId,ct);
        if(row?.Bytes is null) return null;
        var count=await CountReferencesAsync(db,key,ct);
        if(!Enum.TryParse<EvidenceMathStatus>(row.Status,false,out var status)||!Enum.IsDefined(status))
            throw new DataSecurityException("evidence.invalid");
        var result=new StoredEvidence(key,row.Bytes,row.Digest,status,row.ExpiresAt,row.LegalHold,count);
        await tx.CommitAsync(ct);
        return result;
    }

    public async ValueTask<bool> CommitExportAuditAsync(EvidenceKey key,string originalSha256,MovementAudit audit,CancellationToken ct)
    {
        ValidateAudit(key,audit,"RedactedExport");
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var row=await LockedAsync(db,key,ct);
        var now=await DatabaseNowAsync(db,ct);
        if(row?.Bytes is null||row.Digest!=originalSha256||row.ExpiresAt<=now||
            Convert.ToHexStringLower(SHA256.HashData(row.Bytes))!=originalSha256) return false;
        Append(db,audit,now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async ValueTask<RetentionResult> ExpireAsync(EvidenceKey key,MovementAudit audit,CancellationToken ct)
    {
        ValidateAudit(key,audit,"RetentionExpired");
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var row=await LockedAsync(db,key,ct);
        if(row?.Bytes is null) return RetentionResult.Missing;
        var now=await DatabaseNowAsync(db,ct);
        if(row.ExpiresAt>now) return RetentionResult.NotExpired;
        if(row.LegalHold||await CountReferencesAsync(db,key,ct)!=0) return RetentionResult.Protected;
        row.Bytes=null;
        row.PurgedAt=now;
        Append(db,audit,now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return RetentionResult.Removed;
    }

    // All reference mutations lock the same parent row as expiry. Never let a caller insert raw references.
    public async ValueTask<bool> AcquireReferenceAsync(EvidenceKey key,string referenceId,CancellationToken ct=default)
    {
        Validate(key); ValidateId(referenceId);
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var row=await LockedAsync(db,key,ct);
        if(row?.Bytes is null) return false;
        if(await db.References.AnyAsync(x=>x.Tenant==key.TenantId&&x.ModelId==key.ModelId&&x.RevisionId==key.RevisionId&&
            x.EvidenceId==key.EvidenceId&&x.ReferenceId==referenceId,ct)) return true;
        if(row.ExpiresAt<=await DatabaseNowAsync(db,ct)) return false;
        db.References.Add(new(){Tenant=key.TenantId,ModelId=key.ModelId,RevisionId=key.RevisionId,EvidenceId=key.EvidenceId,ReferenceId=referenceId});
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async ValueTask<bool> ReleaseReferenceAsync(EvidenceKey key,string referenceId,CancellationToken ct=default)
    {
        Validate(key); ValidateId(referenceId);
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        if(await LockedAsync(db,key,ct) is null) return false;
        var count=await db.References.Where(x=>x.Tenant==key.TenantId&&x.ModelId==key.ModelId&&x.RevisionId==key.RevisionId&&
            x.EvidenceId==key.EvidenceId&&x.ReferenceId==referenceId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return count==1;
    }

    // Caller is a trusted legal-hold service with a separately authorized actor, not a client supplied audit record.
    public async ValueTask<bool> SetLegalHoldAsync(EvidenceKey key,bool enabled,MovementAudit audit,CancellationToken ct=default)
    {
        ValidateAudit(key,audit,enabled?"HoldSet":"HoldReleased");
        await using var db=new SecurityDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,ct);
        var row=await LockedAsync(db,key,ct);
        if(row?.Bytes is null) return false;
        if(row.LegalHold==enabled) return true;
        var now=await DatabaseNowAsync(db,ct);
        row.LegalHold=enabled;
        row.HoldChangedAt=now;
        Append(db,audit,now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<ArtifactRow?> LockedAsync(SecurityDb db,EvidenceKey key,CancellationToken ct)
    {
        var rows=await db.Artifacts.FromSqlInterpolated($"""
            SELECT * FROM wowdi_security.artifacts WHERE tenant={key.TenantId} AND model_id={key.ModelId}
            AND revision_id={key.RevisionId} AND evidence_id={key.EvidenceId} FOR UPDATE
            """).ToListAsync(ct);
        return rows.SingleOrDefault();
    }
    private static Task<int> CountReferencesAsync(SecurityDb db,EvidenceKey key,CancellationToken ct)=>
        db.References.CountAsync(x=>x.Tenant==key.TenantId&&x.ModelId==key.ModelId&&x.RevisionId==key.RevisionId&&x.EvidenceId==key.EvidenceId,ct);
    private static Task<DateTimeOffset> DatabaseNowAsync(SecurityDb db,CancellationToken ct)=>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
    private static void Append(SecurityDb db,MovementAudit audit,DateTimeOffset now)=>
        db.Audits.Add(new(){Tenant=audit.Key.TenantId,ModelId=audit.Key.ModelId,RevisionId=audit.Key.RevisionId,
            EvidenceId=audit.Key.EvidenceId,AuditId=Guid.NewGuid(),Issuer=audit.Issuer,Subject=audit.Subject,Action=audit.Action,Timestamp=now});
    private static void Validate(EvidenceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
    }
    private static void ValidateId(string id)
    {
        if(id is not {Length:>0 and <=256}||!id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.'))
            throw new DataSecurityException("evidence.key");
    }
    private static void ValidateAudit(EvidenceKey key,MovementAudit audit,string action)
    {
        Validate(key);
        ArgumentNullException.ThrowIfNull(audit);
        if(audit.Key!=key||audit.Action!=action||!AuditId(audit.Issuer)||!AuditId(audit.Subject))
            throw new DataSecurityException("evidence.audit");
    }
    private static bool AuditId(string s)=>s is {Length:>0 and <=512} && s==s.Trim() && !s.Any(char.IsControl);
}
