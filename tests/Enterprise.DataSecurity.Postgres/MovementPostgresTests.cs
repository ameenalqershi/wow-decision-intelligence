using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WowDi.Enterprise.Persistence;
using Xunit;

namespace WowDi.Enterprise.DataSecurity.Postgres.Tests;

public sealed partial class MovementPostgresTests:IAsyncLifetime
{
    private string _admin=null!,_name=null!,_connection=null!;
    private DbContextOptions<SecurityDb> _options=null!;
    private PostgresEvidenceMovementStore _store=null!;
    private static EvidenceKey Key(string tenant="fixture",string id="e1")=>new(tenant,"factory","r17",id);
    private static MovementAudit AuditFor(EvidenceKey key,string action="RetentionExpired")=>
        new(key,"https://issuer.example.test","fixture-actor",action,DateTimeOffset.UtcNow);

    public async Task InitializeAsync()
    {
        _admin=Environment.GetEnvironmentVariable("WOWDI_PG")??
            throw new InvalidOperationException("WOWDI_PG must identify a disposable PostgreSQL test server with CREATEDB.");
        _name="wowdi_sec01_"+Guid.NewGuid().ToString("N");
        await using(var conn=new NpgsqlConnection(_admin))
        {
            await conn.OpenAsync();
            await using var cmd=new NpgsqlCommand($"CREATE DATABASE {_name}",conn);
            await cmd.ExecuteNonQueryAsync();
        }
        _connection=new NpgsqlConnectionStringBuilder(_admin){Database=_name,Pooling=false}.ConnectionString;
        await using(var db=new EnterpriseDb(EnterpriseDb.Configure(_connection))) await db.Database.MigrateAsync();
        _options=SecurityDb.Configure(_connection);
        await using(var db=new SecurityDb(_options)) await db.Database.MigrateAsync();
        _store=new(_options);
        // Minimal FK anchor fixture. E02 domain/registry behavior is covered by its own real database suite.
        await Execute("""
            INSERT INTO wowdi.revisions (tenant,model_id,revision_id,model,graph,model_hash,graph_hash)
            VALUES ('fixture','factory','r17',decode('00','hex'),decode('00','hex'),repeat('0',64),repeat('0',64)),
                   ('other','factory','r17',decode('00','hex'),decode('00','hex'),repeat('0',64),repeat('0',64));
            """);
    }

    public async Task DisposeAsync()
    {
        if(_admin is null||_name is null)return;
        await using var conn=new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd=new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)",conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static StoredEvidence Evidence(EvidenceKey key,DateTimeOffset expires,bool held=false)
    {
        var bytes=Encoding.UTF8.GetBytes("synthetic confidential fixture");
        return new(key,bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)),EvidenceMathStatus.Feasible,expires,held,0);
    }
    private async Task<long> Count(string sql)
    {
        await using var conn=new NpgsqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd=new NpgsqlCommand(sql,conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
    private async Task Execute(string sql)
    {
        await using var conn=new NpgsqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd=new NpgsqlCommand(sql,conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task MigrationsForeignKeysAndTombstones()
    {
        await using(var db=new SecurityDb(_options))
        {
            Assert.Equal(2,(await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.MigrateAsync();
        }
        var evidence=Evidence(Key(),DateTimeOffset.UtcNow.AddHours(-1));
        await _store.RegisterPublishedAsync(evidence);
        await _store.RegisterPublishedAsync(evidence);
        var missing=await Assert.ThrowsAsync<PostgresException>(async()=>
            await _store.RegisterPublishedAsync(Evidence(Key("unregistered"),DateTimeOffset.UtcNow.AddHours(1))));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,missing.SqlState);
        Assert.Equal("evidence.conflict",(await Assert.ThrowsAsync<DataSecurityException>(async()=>
            await _store.RegisterPublishedAsync(evidence with {Status=EvidenceMathStatus.Unknown}))).Code);
        var direct=await Assert.ThrowsAsync<PostgresException>(()=>Execute("UPDATE wowdi_security.artifacts SET bytes=NULL,purged_at=clock_timestamp() WHERE tenant='fixture'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,direct.SqlState);
        Assert.NotNull(await _store.ReadAsync(Key(),default));
        Assert.Equal(RetentionResult.Removed,await _store.ExpireAsync(Key(),AuditFor(Key()),default));
        var immutable=await Assert.ThrowsAsync<PostgresException>(()=>Execute("DELETE FROM wowdi_security.movement_audit WHERE tenant='fixture'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,immutable.SqlState);
        var reference=await Assert.ThrowsAsync<PostgresException>(()=>Execute("INSERT INTO wowdi_security.artifact_references(tenant,model_id,revision_id,evidence_id,reference_id) VALUES('fixture','factory','r17','e1','dangling')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,reference.SqlState);
        Assert.Null(await _store.ReadAsync(Key(),default));
        Assert.Equal("evidence.conflict",(await Assert.ThrowsAsync<DataSecurityException>(async()=>
            await _store.RegisterPublishedAsync(evidence))).Code);
        Assert.False(await _store.AcquireReferenceAsync(Key(),"reference"));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_security.artifacts WHERE bytes IS NULL AND purged_at IS NOT NULL"));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_security.movement_audit"));
    }

    [Fact]
    public async Task ActualTransactionsProtectReferencesHoldsAndOtherTenants()
    {
        var expired=Key(id:"expired");var held=Key(id:"held");var current=Key(id:"current");
        var referenced=Key(id:"referenced");var foreign=Key("other","expired");
        await _store.RegisterPublishedAsync(Evidence(expired,DateTimeOffset.UtcNow.AddHours(-1)));
        await _store.RegisterPublishedAsync(Evidence(held,DateTimeOffset.UtcNow.AddHours(-1),true));
        await _store.RegisterPublishedAsync(Evidence(current,DateTimeOffset.UtcNow.AddHours(1)));
        await _store.RegisterPublishedAsync(Evidence(foreign,DateTimeOffset.UtcNow.AddHours(-1)));
        var expiry=DateTimeOffset.UtcNow.AddSeconds(5);
        await _store.RegisterPublishedAsync(Evidence(referenced,expiry));
        Assert.True(await _store.AcquireReferenceAsync(referenced,"decision-1"));
        Assert.True(await _store.AcquireReferenceAsync(referenced,"decision-1"));
        Assert.Equal(1,(await _store.ReadAsync(referenced,default))!.ReferenceCount);
        Assert.Equal(RetentionResult.Protected,await _store.ExpireAsync(held,AuditFor(held),default));
        Assert.Equal(RetentionResult.NotExpired,await _store.ExpireAsync(current,AuditFor(current),default));
        var delay=expiry-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(200);
        if(delay>TimeSpan.Zero)await Task.Delay(delay);
        Assert.Equal(RetentionResult.Protected,await _store.ExpireAsync(referenced,AuditFor(referenced),default));
        Assert.False(await _store.AcquireReferenceAsync(referenced,"late"));
        var attempts=await Task.WhenAll(Enumerable.Range(0,20)
            .Select(_=>_store.ExpireAsync(expired,AuditFor(expired),default).AsTask()));
        Assert.Single(attempts,x=>x==RetentionResult.Removed);
        Assert.Equal(19,attempts.Count(x=>x==RetentionResult.Missing));
        Assert.NotNull(await _store.ReadAsync(foreign,default));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE evidence_id='expired' AND action='RetentionExpired'"));
        Assert.True(await _store.ReleaseReferenceAsync(referenced,"decision-1"));
        Assert.Equal(RetentionResult.Removed,await _store.ExpireAsync(referenced,AuditFor(referenced),default));
        Assert.True(await _store.SetLegalHoldAsync(held,false,AuditFor(held,"HoldReleased")));
        Assert.Equal(RetentionResult.Removed,await _store.ExpireAsync(held,AuditFor(held),default));
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_security.artifact_references"));
        Assert.Equal(3L,await Count("SELECT count(*) FROM wowdi_security.movement_audit WHERE action='RetentionExpired'"));
    }

    [Fact]
    public async Task ExportCommitAndAuditFailureAreAtomic()
    {
        var live=Evidence(Key(),DateTimeOffset.UtcNow.AddHours(1));
        var expired=Evidence(Key(id:"expired"),DateTimeOffset.UtcNow.AddHours(-1));
        await _store.RegisterPublishedAsync(live);
        await _store.RegisterPublishedAsync(expired);
        Assert.True(await _store.CommitExportAuditAsync(live.Key,live.OriginalSha256,AuditFor(live.Key,"RedactedExport"),default));
        Assert.False(await _store.CommitExportAuditAsync(expired.Key,expired.OriginalSha256,AuditFor(expired.Key,"RedactedExport"),default));
        Assert.False(await _store.CommitExportAuditAsync(live.Key,new string('0',64),AuditFor(live.Key,"RedactedExport"),default));
        await Execute("""
            CREATE FUNCTION wowdi_security.fixture_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture failure'; END $$;
            CREATE TRIGGER fixture_failure BEFORE INSERT ON wowdi_security.movement_audit FOR EACH ROW EXECUTE FUNCTION wowdi_security.fixture_failure();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(async()=>await _store.ExpireAsync(expired.Key,AuditFor(expired.Key),default));
        Assert.NotNull(await _store.ReadAsync(expired.Key,default));
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_security.artifacts WHERE bytes IS NULL"));
        await Assert.ThrowsAsync<DbUpdateException>(async()=>await _store.CommitExportAuditAsync(live.Key,live.OriginalSha256,AuditFor(live.Key,"RedactedExport"),default));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_security.movement_audit"));
    }
}
