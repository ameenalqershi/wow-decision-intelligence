using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace WowDi.Enterprise.Operations;

internal sealed class DenialRow
{
    public Guid Id {get;set;}
    public string? Tenant {get;set;}
    public string? SubjectDigest {get;set;}
    public string CorrelationDigest {get;set;}="";
    public string Action {get;set;}="";
    public DateTimeOffset RecordedAt {get;set;}
}
internal sealed class UsageRow
{
    public string Tenant {get;set;}="";
    public string ModelId {get;set;}="";
    public string RevisionId {get;set;}="";
    public string JobId {get;set;}="";
    public Guid Epoch {get;set;}
    public long Fence {get;set;}
    public string InputSha256 {get;set;}="";
    public int Threads {get;set;}
    public long MemoryBudgetBytes {get;set;}
    public long ElapsedMs {get;set;}
    public long ReservedCoreMs {get;set;}
    public long? CpuMs {get;set;}
    public long? ScopePeakBytes {get;set;}
    public string? ScopePeakMetric {get;set;}
    public string Termination {get;set;}="";
    public DateTimeOffset RecordedAt {get;set;}
}
public sealed class OperationsDb(DbContextOptions<OperationsDb> options):DbContext(options)
{
    internal DbSet<DenialRow> Denials=>Set<DenialRow>();
    internal DbSet<UsageRow> Usage=>Set<UsageRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("wowdi_ops");
        model.Entity<DenialRow>(e=>
        {
            e.ToTable("denials", t=>t.HasCheckConstraint("denial_digest","(subject_digest IS NULL OR subject_digest ~ '^[0-9a-f]{64}$') AND correlation_digest ~ '^[0-9a-f]{64}$'"));
            e.HasKey(x=>x.Id);
            e.Property(x=>x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x=>x.Tenant).HasColumnName("tenant").HasMaxLength(512).UseCollation("C");
            e.Property(x=>x.SubjectDigest).HasColumnName("subject_digest").HasMaxLength(64);
            e.Property(x=>x.CorrelationDigest).HasColumnName("correlation_digest").HasMaxLength(64);
            e.Property(x=>x.Action).HasColumnName("action").HasMaxLength(32);
            e.Property(x=>x.RecordedAt).HasColumnName("recorded_at");
            e.HasIndex(x=>new{x.Tenant,x.RecordedAt});
        });
        model.Entity<UsageRow>(e=>
        {
            e.ToTable("run_usage",t=>t.HasCheckConstraint("usage_values","fence > 0 AND threads > 0 AND memory_budget_bytes > 0 AND elapsed_ms >= 0 AND reserved_core_ms >= 0 AND reserved_core_ms::numeric = elapsed_ms::numeric * threads AND (cpu_ms IS NULL OR cpu_ms >= 0) AND (scope_peak_bytes IS NULL OR scope_peak_bytes >= 0) AND ((scope_peak_bytes IS NULL AND scope_peak_metric IS NULL) OR (scope_peak_bytes IS NOT NULL AND scope_peak_metric IS NOT NULL AND scope_peak_metric IN ('cgroup-v2-charged-memory','windows-job-commit'))) AND input_sha256 ~ '^[0-9a-f]{64}$' AND termination IN ('Completed','Failed','Cancelled','Timeout')"));
            e.HasKey(x=>new{x.Tenant,x.JobId,x.Epoch,x.Fence});
            e.Property(x=>x.Tenant).HasColumnName("tenant").HasMaxLength(256).UseCollation("C");
            e.Property(x=>x.ModelId).HasColumnName("model_id").HasMaxLength(256).UseCollation("C");
            e.Property(x=>x.RevisionId).HasColumnName("revision_id").HasMaxLength(256).UseCollation("C");
            e.Property(x=>x.JobId).HasColumnName("job_id").HasMaxLength(256).UseCollation("C");
            e.Property(x=>x.Epoch).HasColumnName("epoch");
            e.Property(x=>x.Fence).HasColumnName("fence");
            e.Property(x=>x.InputSha256).HasColumnName("input_sha256").HasMaxLength(64);
            e.Property(x=>x.Threads).HasColumnName("threads");
            e.Property(x=>x.MemoryBudgetBytes).HasColumnName("memory_budget_bytes");
            e.Property(x=>x.ElapsedMs).HasColumnName("elapsed_ms");
            e.Property(x=>x.ReservedCoreMs).HasColumnName("reserved_core_ms");
            e.Property(x=>x.CpuMs).HasColumnName("cpu_ms");
            e.Property(x=>x.ScopePeakBytes).HasColumnName("scope_peak_bytes");
            e.Property(x=>x.ScopePeakMetric).HasColumnName("scope_peak_metric").HasMaxLength(32);
            e.Property(x=>x.Termination).HasColumnName("termination").HasMaxLength(16);
            e.Property(x=>x.RecordedAt).HasColumnName("recorded_at");
            e.HasIndex(x=>new{x.Tenant,x.ModelId,x.RecordedAt});
        });
    }
    public static DbContextOptions<OperationsDb> Configure(string connectionString)=>new DbContextOptionsBuilder<OperationsDb>().UseNpgsql(connectionString,o=>o.MigrationsHistoryTable("__ef_migrations","wowdi_ops")).Options;
}
public sealed class OperationsDesignFactory:IDesignTimeDbContextFactory<OperationsDb>
{
    public OperationsDb CreateDbContext(string[] args)=>new(OperationsDb.Configure(Environment.GetEnvironmentVariable("WOWDI_PG")??"Host=127.0.0.1;Database=wowdi_design;Username=wowdi_design"));
}
