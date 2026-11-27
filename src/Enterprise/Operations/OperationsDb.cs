using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace WowDi.Enterprise.Operations;

internal sealed class DenialRow
{
    public Guid Id { get; set; }
    public string? Tenant { get; set; }
    public string? SubjectDigest { get; set; }
    public string CorrelationDigest { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTimeOffset RecordedAt { get; set; }
}
public sealed class OperationsDb(DbContextOptions<OperationsDb> options):DbContext(options)
{
    internal DbSet<DenialRow> Denials => Set<DenialRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("wowdi_ops");
        model.Entity<DenialRow>(e =>
        {
            e.ToTable("denials", t => t.HasCheckConstraint("denial_digest", "(subject_digest IS NULL OR subject_digest ~ '^[0-9a-f]{64}$') AND correlation_digest ~ '^[0-9a-f]{64}$'"));
            e.HasKey(x=>x.Id);
            e.Property(x=>x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x=>x.Tenant).HasColumnName("tenant").HasMaxLength(512).UseCollation("C");
            e.Property(x=>x.SubjectDigest).HasColumnName("subject_digest").HasMaxLength(64);
            e.Property(x=>x.CorrelationDigest).HasColumnName("correlation_digest").HasMaxLength(64);
            e.Property(x=>x.Action).HasColumnName("action").HasMaxLength(32);
            e.Property(x=>x.RecordedAt).HasColumnName("recorded_at");
            e.HasIndex(x=>new{x.Tenant,x.RecordedAt});
        });
    }
    public static DbContextOptions<OperationsDb> Configure(string connectionString)=>new DbContextOptionsBuilder<OperationsDb>().UseNpgsql(connectionString,o=>o.MigrationsHistoryTable("__ef_migrations","wowdi_ops")).Options;
}
public sealed class OperationsDesignFactory:IDesignTimeDbContextFactory<OperationsDb>
{
    public OperationsDb CreateDbContext(string[] args)=>new(OperationsDb.Configure(Environment.GetEnvironmentVariable("WOWDI_PG")??"Host=127.0.0.1;Database=wowdi_design;Username=wowdi_design"));
}
