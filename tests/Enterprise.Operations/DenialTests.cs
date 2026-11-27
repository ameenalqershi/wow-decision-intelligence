using Microsoft.EntityFrameworkCore;
using Npgsql;
using WowDi.Enterprise.Identity;
using Xunit;

namespace WowDi.Enterprise.Operations.Tests;
public sealed class DenialTests:IAsyncLifetime
{
    private string _admin=null!,_db=null!,_connection=null!;
    private static readonly byte[] Key=Enumerable.Range(1,32).Select(x=>(byte)x).ToArray();
    public async Task InitializeAsync()
    {
        _admin=Environment.GetEnvironmentVariable("WOWDI_PG")??throw new InvalidOperationException("Disposable PostgreSQL required.");
        _db="wowdi_e05_"+Guid.NewGuid().ToString("N");
        await using var c=new NpgsqlConnection(_admin);await c.OpenAsync();
        await using var cmd=new NpgsqlCommand($"CREATE DATABASE {_db}",c);await cmd.ExecuteNonQueryAsync();
        _connection=new NpgsqlConnectionStringBuilder(_admin){Database=_db,Pooling=false}.ConnectionString;
        await using var db=new OperationsDb(OperationsDb.Configure(_connection));await db.Database.MigrateAsync();
    }
    public async Task DisposeAsync()
    {
        if(_admin is null||_db is null)return;
        await using var c=new NpgsqlConnection(_admin);await c.OpenAsync();
        await using var cmd=new NpgsqlCommand($"DROP DATABASE IF EXISTS {_db} WITH (FORCE)",c);await cmd.ExecuteNonQueryAsync();
    }
    [Fact]
    public async Task ConcurrentDurableRecordsAreTenantSeparatedAndRedacted()
    {
        var options=OperationsDb.Configure(_connection);
        var audit=new DurableDenialAudit(options,Key,TimeProvider.System);
        await Task.WhenAll(Enumerable.Range(0,24).Select(i=>audit.RecordAsync(new TenantDenial(i%2==0?"tenant-a":"tenant-b","private-person","View","private-correlation",DateTimeOffset.UnixEpoch),default).AsTask()));
        await audit.RecordAsync(new(null,"raw-unknown-subject","raw-injected-action","raw-correlation",DateTimeOffset.UnixEpoch),default);
        await using var c=new NpgsqlConnection(_connection);await c.OpenAsync();
        await using var cmd=new NpgsqlCommand("SELECT tenant,subject_digest,correlation_digest,action,recorded_at FROM wowdi_ops.denials",c);
        await using var r=await cmd.ExecuteReaderAsync();
        var hashes=new Dictionary<string,string>();
        int count=0;
        while(await r.ReadAsync())
        {
            count++;
            Assert.Equal(64,r.GetString(2).Length);
            Assert.True(r.GetFieldValue<DateTime>(4)>DateTime.UnixEpoch);
            if(r.IsDBNull(0)){Assert.True(r.IsDBNull(1));Assert.Equal("Unknown",r.GetString(3));continue;}
            var tenant=r.GetString(0);var hash=r.GetString(1);
            Assert.Equal("View",r.GetString(3));Assert.Equal(64,hash.Length);
            if(hashes.TryGetValue(tenant,out var previous))Assert.Equal(previous,hash);else hashes.Add(tenant,hash);
        }
        Assert.Equal(25,count);Assert.Equal(2,hashes.Count);Assert.NotEqual(hashes["tenant-a"],hashes["tenant-b"]);
    }
    [Fact]
    public async Task MigrationIsStableAndAuditFailureIsNotSwallowed()
    {
        var options=OperationsDb.Configure(_connection);
        await using(var db=new OperationsDb(options))
        {Assert.False(db.Database.HasPendingModelChanges());await db.Database.MigrateAsync();Assert.Single(await db.Database.GetAppliedMigrationsAsync());}
        await using(var c=new NpgsqlConnection(_connection))
        {await c.OpenAsync();await using var cmd=new NpgsqlCommand("DROP TABLE wowdi_ops.denials",c);await cmd.ExecuteNonQueryAsync();}
        var audit=new DurableDenialAudit(options,Key,TimeProvider.System);
        await Assert.ThrowsAsync<DbUpdateException>(()=>audit.RecordAsync(new("tenant-a","subject","View","correlation",DateTimeOffset.UtcNow),default).AsTask());
    }
}
