using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace WowDi.Enterprise.DataSecurity.Postgres.Tests;

public sealed partial class MovementPostgresTests
{
    [Fact]
    public async Task ConcurrentExportsPreserveTenantAuditAndCancelledRetentionRollsBack()
    {
        var a=Evidence(Key(),DateTimeOffset.UtcNow.AddHours(1));
        var b=Evidence(Key("other"),DateTimeOffset.UtcNow.AddHours(1));
        var expired=Evidence(Key(id:"expired"),DateTimeOffset.UtcNow.AddHours(-1));
        foreach(var item in new[]{a,b,expired}) await _store.RegisterPublishedAsync(item);
        var exports=Enumerable.Range(0,32).Select(i=>
        {
            var item=i%2==0?a:b;
            return _store.CommitExportAuditAsync(item.Key,item.OriginalSha256,AuditFor(item.Key,"RedactedExport"),default).AsTask();
        });
        Assert.All(await Task.WhenAll(exports),result=>Assert.True(result));
        Assert.Equal(16L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='fixture' AND action='RedactedExport'"));
        Assert.Equal(16L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='other' AND action='RedactedExport'"));
        await using(var connection=new NpgsqlConnection(_connection))
        {
            await connection.OpenAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            await using var cmd=new NpgsqlCommand("SELECT 1 FROM wowdi_security.artifacts WHERE tenant='fixture' AND evidence_id='expired' FOR UPDATE",connection,transaction);
            await cmd.ExecuteScalarAsync();
            using var cts=new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>
                await _store.ExpireAsync(expired.Key,AuditFor(expired.Key),cts.Token));
            await transaction.RollbackAsync();
        }
        Assert.NotNull(await _store.ReadAsync(expired.Key,default));
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE action='RetentionExpired'"));
        Assert.Equal(RetentionResult.Removed,await _store.ExpireAsync(expired.Key,AuditFor(expired.Key),default));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE action='RetentionExpired'"));
    }
}
