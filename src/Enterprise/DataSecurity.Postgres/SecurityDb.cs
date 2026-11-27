using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WowDi.Enterprise.DataSecurity.Postgres;

internal class ScopeRow
{
    public string Tenant { get; set; }="";
    public string ModelId { get; set; }="";
    public string RevisionId { get; set; }="";
}
internal sealed class RegistryRevision : ScopeRow;
internal class EvidenceScopeRow : ScopeRow
{
    public string EvidenceId { get; set; }="";
}
internal sealed class ArtifactRow : EvidenceScopeRow
{
    public byte[]? Bytes { get; set; }
    public string Digest { get; set; }="";
    public string Status { get; set; }="";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool LegalHold { get; set; }
    public DateTimeOffset? HoldChangedAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
}
internal sealed class ReferenceRow : EvidenceScopeRow
{
    public string ReferenceId { get; set; }="";
}
internal sealed class AuditRow : EvidenceScopeRow
{
    public Guid AuditId { get; set; }
    public string Issuer { get; set; }="";
    public string Subject { get; set; }="";
    public string Action { get; set; }="";
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class SecurityDb(DbContextOptions<SecurityDb> options):DbContext(options)
{
    internal DbSet<ArtifactRow> Artifacts=>Set<ArtifactRow>();
    internal DbSet<ReferenceRow> References=>Set<ReferenceRow>();
    internal DbSet<AuditRow> Audits=>Set<AuditRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("wowdi_security");
        var revision=model.Entity<RegistryRevision>();
        revision.ToTable("revisions","wowdi",t=>t.ExcludeFromMigrations());
        revision.HasKey(x=>new{x.Tenant,x.ModelId,x.RevisionId});

        var artifact=model.Entity<ArtifactRow>();
        artifact.ToTable("artifacts",t=>
        {
            t.HasCheckConstraint("CK_artifact_tombstone","(bytes IS NOT NULL AND purged_at IS NULL) OR (bytes IS NULL AND purged_at IS NOT NULL)");
            t.HasCheckConstraint("CK_artifact_size","bytes IS NULL OR octet_length(bytes) BETWEEN 1 AND 16777216");
            t.HasCheckConstraint("CK_artifact_digest","digest ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("CK_artifact_status","status IN ('Optimal','Feasible','Infeasible','Unbounded','Unknown')");
        });
        artifact.HasKey(x=>new{x.Tenant,x.ModelId,x.RevisionId,x.EvidenceId});
        artifact.HasOne<RegistryRevision>().WithMany().HasForeignKey(x=>new{x.Tenant,x.ModelId,x.RevisionId}).OnDelete(DeleteBehavior.Restrict);
        artifact.Property(x=>x.Bytes).HasColumnName("bytes");
        artifact.Property(x=>x.Digest).HasColumnName("digest").HasMaxLength(64);
        artifact.Property(x=>x.Status).HasColumnName("status").HasMaxLength(16);
        artifact.Property(x=>x.ExpiresAt).HasColumnName("expires_at");
        artifact.Property(x=>x.LegalHold).HasColumnName("legal_hold");
        artifact.Property(x=>x.HoldChangedAt).HasColumnName("hold_changed_at");
        artifact.Property(x=>x.PurgedAt).HasColumnName("purged_at");

        var reference=model.Entity<ReferenceRow>();
        reference.ToTable("artifact_references");
        reference.HasKey(x=>new{x.Tenant,x.ModelId,x.RevisionId,x.EvidenceId,x.ReferenceId});
        reference.HasOne<ArtifactRow>().WithMany().HasForeignKey(x=>new{x.Tenant,x.ModelId,x.RevisionId,x.EvidenceId}).OnDelete(DeleteBehavior.Restrict);
        reference.Property(x=>x.ReferenceId).HasColumnName("reference_id").HasMaxLength(256);

        var audit=model.Entity<AuditRow>();
        audit.ToTable("movement_audit",t=>t.HasCheckConstraint("CK_movement_action","action IN ('RedactedExport','RetentionExpired','HoldSet','HoldReleased')"));
        audit.HasKey(x=>new{x.Tenant,x.ModelId,x.RevisionId,x.EvidenceId,x.AuditId});
        audit.HasOne<ArtifactRow>().WithMany().HasForeignKey(x=>new{x.Tenant,x.ModelId,x.RevisionId,x.EvidenceId}).OnDelete(DeleteBehavior.Restrict);
        audit.Property(x=>x.AuditId).HasColumnName("audit_id").ValueGeneratedNever();
        audit.Property(x=>x.Issuer).HasColumnName("issuer").HasMaxLength(512);
        audit.Property(x=>x.Subject).HasColumnName("subject").HasMaxLength(512);
        audit.Property(x=>x.Action).HasColumnName("action").HasMaxLength(32);
        audit.Property(x=>x.Timestamp).HasColumnName("recorded_at");
        foreach(var entity in model.Model.GetEntityTypes())
        {
            foreach(var p in entity.GetProperties())
            {
                if(p.Name is "Tenant" or "ModelId" or "RevisionId" or "EvidenceId")
                {
                    p.SetColumnName(p.Name switch {"Tenant"=>"tenant","ModelId"=>"model_id","RevisionId"=>"revision_id",_=>"evidence_id"});
                    p.SetMaxLength(256);
                }
                if(p.ClrType==typeof(string)) p.SetCollation("C");
            }
        }
    }
    public static DbContextOptions<SecurityDb> Configure(string connectionString)=>
        new DbContextOptionsBuilder<SecurityDb>().UseNpgsql(connectionString,
            o=>o.MigrationsHistoryTable("__security_migrations","wowdi_security")).Options;
}
public sealed class SecurityDesignFactory:IDesignTimeDbContextFactory<SecurityDb>
{
    public SecurityDb CreateDbContext(string[] args)=>new(SecurityDb.Configure(
        Environment.GetEnvironmentVariable("WOWDI_PG")??"Host=127.0.0.1;Database=wowdi_design;Username=wowdi_design"));
}
