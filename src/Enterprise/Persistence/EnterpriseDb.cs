using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WowDi.Enterprise.Persistence;

// Infrastructure only: register scoped repository; never expose raw DbContext to endpoints.
internal sealed class RevisionRow
{
    public string Tenant { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public byte[] Model { get; set; } = [];
    public byte[] Graph { get; set; } = [];
    public string ModelHash { get; set; } = "";
    public string GraphHash { get; set; } = "";
}
internal sealed class ContentRow
{
    public string Tenant { get; set; } = "";
    public string Hash { get; set; } = "";
    public byte[] Data { get; set; } = [];
}
internal sealed class InputRow
{
    public string Tenant { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string Hash { get; set; } = "";
}
internal sealed class JobRow
{
    public string Tenant { get; set; } = "";
    public string JobId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string RevisionId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public byte[] Request { get; set; } = [];
    public string Issuer { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = "Queued";
}

public sealed class EnterpriseDb(DbContextOptions<EnterpriseDb> options) : DbContext(options)
{
    internal DbSet<RevisionRow> Revisions => Set<RevisionRow>();
    internal DbSet<ContentRow> Content => Set<ContentRow>();
    internal DbSet<InputRow> Inputs => Set<InputRow>();
    internal DbSet<JobRow> Jobs => Set<JobRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("wowdi");
        model.Entity<RevisionRow>(e =>
        {
            e.ToTable("revisions");
            e.HasKey(x => new { x.Tenant, x.ModelId, x.RevisionId });
            e.Property(x => x.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(x => x.ModelId).HasColumnName("model_id").HasMaxLength(256);
            e.Property(x => x.RevisionId).HasColumnName("revision_id").HasMaxLength(256);
            e.Property(x => x.Model).HasColumnName("model");
            e.Property(x => x.Graph).HasColumnName("graph");
            e.Property(x => x.ModelHash).HasColumnName("model_hash").HasMaxLength(64);
            e.Property(x => x.GraphHash).HasColumnName("graph_hash").HasMaxLength(64);
        });
        model.Entity<ContentRow>(e =>
        {
            e.ToTable("content");
            e.HasKey(x => new { x.Tenant, x.Hash });
            e.Property(x => x.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(64);
            e.Property(x => x.Data).HasColumnName("data");
        });
        model.Entity<InputRow>(e =>
        {
            e.ToTable("inputs");
            e.HasKey(x => new { x.Tenant, x.ModelId, x.RevisionId, x.Hash });
            e.Property(x => x.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(x => x.ModelId).HasColumnName("model_id").HasMaxLength(256);
            e.Property(x => x.RevisionId).HasColumnName("revision_id").HasMaxLength(256);
            e.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(64);
            e.HasOne<RevisionRow>().WithMany().HasForeignKey(x => new { x.Tenant, x.ModelId, x.RevisionId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ContentRow>().WithMany().HasForeignKey(x => new { x.Tenant, x.Hash }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<JobRow>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => new { x.Tenant, x.JobId });
            e.Property(x => x.Tenant).HasColumnName("tenant").HasMaxLength(256);
            e.Property(x => x.JobId).HasColumnName("job_id").HasMaxLength(256);
            e.Property(x => x.ModelId).HasColumnName("model_id").HasMaxLength(256);
            e.Property(x => x.RevisionId).HasColumnName("revision_id").HasMaxLength(256);
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(256);
            e.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            e.Property(x => x.Request).HasColumnName("request");
            e.Property(x => x.Issuer).HasColumnName("issuer").HasMaxLength(512);
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(512);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(32);
            e.HasIndex(x => new { x.Tenant, x.IdempotencyKey }).IsUnique();
            e.HasOne<RevisionRow>().WithMany().HasForeignKey(x => new { x.Tenant, x.ModelId, x.RevisionId }).OnDelete(DeleteBehavior.Restrict);
        });
        // Specify ordinal collation on every textual key; do not rely on database locale.
        foreach (var property in model.Model.GetEntityTypes().SelectMany(e => e.GetProperties()).Where(p => p.ClrType == typeof(string)))
            property.SetCollation("C");
    }

    public static DbContextOptions<EnterpriseDb> Configure(string connectionString) =>
        new DbContextOptionsBuilder<EnterpriseDb>().UseNpgsql(connectionString,
            o => o.MigrationsHistoryTable("__ef_migrations", "wowdi")).Options;
}

// Only used by EF migration generation; never connects unless an explicit string is provided.
public sealed class EnterpriseDesignFactory : IDesignTimeDbContextFactory<EnterpriseDb>
{
    public EnterpriseDb CreateDbContext(string[] args) =>
        new(EnterpriseDb.Configure(Environment.GetEnvironmentVariable("WOWDI_PG") ??
            "Host=127.0.0.1;Database=wowdi_design;Username=wowdi_design"));
}
