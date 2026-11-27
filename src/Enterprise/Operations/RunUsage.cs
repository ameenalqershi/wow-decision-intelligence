using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WowDi.Enterprise.Identity;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;

namespace WowDi.Enterprise.Operations;

public sealed record RunUsage(string TenantId,string ModelId,string RevisionId,string JobId,Guid Epoch,long Fence,
    string InputSha256,int Threads,long MemoryBudgetBytes,long ElapsedMs,long ReservedCoreMs,long? CpuMs,
    long? ScopePeakBytes,string? ScopePeakMetric,Termination Termination);
public sealed record UsageTotals(long Attempts,decimal ReservedCoreMs,decimal? MeasuredCpuMs,long CpuMeasuredAttempts,
    long? PeakScopeBytes,long MemoryMeasuredAttempts,string? ScopePeakMetric);

/// <summary>Trusted worker host port, not a client DTO or an authoritative job-state transition.
/// Does not grant publish rights. Record every cleaned attempt, including failures.</summary>
public sealed class RunUsageRecorder(DbContextOptions<OperationsDb> options)
{
    public static readonly ActivitySource Activities=new("WowDi.Enterprise.Operations","1.0");
    public async Task RecordAsync(WorkerLease lease,BoundaryResult result,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(result);
        if(!result.CleanupConfirmed)throw new InvalidOperationException("Unconfirmed worker cleanup.");
        var key=lease.Request.Key;
        var b=lease.Request.Budget;
        var elapsed=result.Resources.ElapsedMs??throw new InvalidOperationException("Missing elapsed measurement.");
        var usage=new RunUsage(key.TenantId,key.ModelId,key.RevisionId,key.RunId,lease.Epoch,lease.Fence,
            lease.Request.FrozenInputSha256,b.Threads,b.MemoryBytes,elapsed,result.ReservedCoreMilliseconds,
            result.Resources.CpuMs,result.ScopePeakBytes,result.ScopePeakMetric,result.Termination);
        await RecordMeasuredAsync(usage,ct);
    }

    // Internal to host assembly and tests; no wire deserialization of measurements.
    internal async Task RecordMeasuredAsync(RunUsage u,CancellationToken ct=default)
    {
        Validate(u);
        // No identifiers or free text tags. The authenticated durable record carries scoped linkage.
        using var activity=Activities.StartActivity("worker.usage.record");
        activity?.SetTag("worker.termination",u.Termination.ToString());
        activity?.SetTag("worker.cpu.measured",u.CpuMs.HasValue);
        activity?.SetTag("worker.memory.measured",u.ScopePeakBytes.HasValue);
        await using var db=new OperationsDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(ct);
        var state=u.Termination.ToString();
        var trace=activity?.TraceId.ToHexString();
        var inserted=await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO wowdi_ops.run_usage(tenant,model_id,revision_id,job_id,epoch,fence,input_sha256,threads,memory_budget_bytes,elapsed_ms,reserved_core_ms,cpu_ms,scope_peak_bytes,scope_peak_metric,termination,recorded_at,trace_id)
            SELECT j.tenant,j.model_id,j.revision_id,j.job_id,{u.Epoch},{u.Fence},{u.InputSha256},{u.Threads},{u.MemoryBudgetBytes},{u.ElapsedMs},{u.ReservedCoreMs},{u.CpuMs},{u.ScopePeakBytes},{u.ScopePeakMetric},{state},clock_timestamp(),{trace}
            FROM wowdi.jobs j
            WHERE j.tenant={u.TenantId} AND j.model_id={u.ModelId} AND j.revision_id={u.RevisionId} AND j.job_id={u.JobId}
            ON CONFLICT(tenant,job_id,epoch,fence) DO NOTHING
            """,ct);
        if(inserted==0)
        {
            var prior=await db.Usage.AsNoTracking().SingleOrDefaultAsync(x=>x.Tenant==u.TenantId&&x.JobId==u.JobId&&x.Epoch==u.Epoch&&x.Fence==u.Fence,ct);
            if(prior is null||prior.ModelId!=u.ModelId||prior.RevisionId!=u.RevisionId||prior.InputSha256!=u.InputSha256||
                prior.Threads!=u.Threads||prior.MemoryBudgetBytes!=u.MemoryBudgetBytes||prior.ElapsedMs!=u.ElapsedMs||
                prior.ReservedCoreMs!=u.ReservedCoreMs||prior.CpuMs!=u.CpuMs||prior.ScopePeakBytes!=u.ScopePeakBytes||
                prior.ScopePeakMetric!=u.ScopePeakMetric||prior.Termination!=state)
                throw new InvalidOperationException("Usage scope or retry conflict.");
        }
        await tx.CommitAsync(ct);
    }

    private static void Validate(RunUsage u)
    {
        static bool Id(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=256&&value==value.Trim()&&!value.Any(char.IsControl);
        if(!Id(u.TenantId)||!Id(u.ModelId)||!Id(u.RevisionId)||!Id(u.JobId)||u.Epoch==Guid.Empty||u.Fence<=0||
            u.InputSha256 is not {Length:64}||!u.InputSha256.All(char.IsAsciiHexDigitLower)||u.Threads<=0||
            u.MemoryBudgetBytes<=0||u.ElapsedMs<0||u.ElapsedMs>long.MaxValue/u.Threads||
            u.ReservedCoreMs!=u.ElapsedMs*u.Threads||u.CpuMs is <0||u.ScopePeakBytes is <0||
            (u.ScopePeakBytes.HasValue!=(u.ScopePeakMetric is not null))||
            u.ScopePeakMetric is not (null or "cgroup-v2-charged-memory" or "windows-job-commit")||!Enum.IsDefined(u.Termination))
            throw new ArgumentException("Invalid worker measurement.");
    }
}

// Authorization before database lookup. Model-scoped views only, not tenant-admin privileges.
public sealed class UsageReader(DbContextOptions<OperationsDb> options,TenantAccess access)
{
    public async Task<UsageTotals?> ReadAsync(TenantResource scope,DateTimeOffset from,DateTimeOffset until,
        string? peakMetric,CancellationToken ct=default)
    {
        if(await access.AuthorizeAsync(TenantPermission.View,scope,ct) is null)return null;
        if(until<=from||until-from>TimeSpan.FromDays(366)||peakMetric is not (null or "cgroup-v2-charged-memory" or "windows-job-commit"))
            throw new ArgumentException("Invalid usage window.");
        await using var db=new OperationsDb(options);
        await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        var q=db.Usage.AsNoTracking().Where(x=>x.Tenant==scope.TenantId&&x.ModelId==scope.ModelId&&x.RecordedAt>=from.ToUniversalTime()&&x.RecordedAt<until.ToUniversalTime());
        var totals=await q.GroupBy(x=>1).Select(g=>new
        {
            Attempts=g.LongCount(),Reserved=g.Sum(x=>(decimal)x.ReservedCoreMs),
            Cpu=g.Sum(x=>(decimal)(x.CpuMs??0)),Measured=g.LongCount(x=>x.CpuMs!=null)
        }).SingleOrDefaultAsync(ct);
        var memory=q.Where(x=>x.ScopePeakMetric==peakMetric&&x.ScopePeakBytes!=null);
        long? peak=null;long measured=0;
        if(peakMetric is not null){peak=await memory.MaxAsync(x=>x.ScopePeakBytes,ct);measured=await memory.LongCountAsync(ct);}
        await tx.CommitAsync(ct);
        if(await access.AuthorizeAsync(TenantPermission.View,scope,ct) is null)return null;
        return new(totals?.Attempts??0,totals?.Reserved??0,totals is {Measured:>0}?totals.Cpu:null,totals?.Measured??0,peak,measured,peakMetric);
    }
}
