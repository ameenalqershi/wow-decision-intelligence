using Microsoft.EntityFrameworkCore;
namespace WowDi.Enterprise.Operations;

/// <summary>Host-only maintenance port. Separate role granted EXECUTE on expire_denials, never direct DELETE.
/// Policy selects a cutoff; null tenant explicitly selects unauthenticated/unknown-actor records only.</summary>
public sealed class DenialRetention(DbContextOptions<OperationsDb> options)
{
    public async Task<int> ExpireBatchAsync(Guid operationId,string? tenant,DateTimeOffset cutoff,int batchLimit,CancellationToken ct=default)
    {
        if(operationId==Guid.Empty||batchLimit is <1 or >1000||
            tenant is not null&&(string.IsNullOrWhiteSpace(tenant)||tenant.Length>512||tenant!=tenant.Trim()||tenant.Any(char.IsControl)))
            throw new ArgumentException("Invalid retention scope.");
        await using var db=new OperationsDb(options);
        return await db.Database.SqlQuery<int>($"SELECT wowdi_ops.expire_denials({operationId},{tenant}::varchar,{cutoff.ToUniversalTime()},{batchLimit}) AS \"Value\"").SingleAsync(ct);
    }
}
