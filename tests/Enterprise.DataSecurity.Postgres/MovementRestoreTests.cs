using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WowDi.Enterprise.Persistence;
using Xunit;

namespace WowDi.Enterprise.DataSecurity.Postgres.Tests;

public sealed partial class MovementPostgresTests
{
    [Fact]
    public async Task RestoredDatabasePreservesAuditTombstonesReferencesAndRetention()
    {
        var expired=Evidence(Key(id:"purged"),DateTimeOffset.UtcNow.AddHours(-1));
        var held=Evidence(Key(id:"held"),DateTimeOffset.UtcNow.AddHours(-1),true);
        var active=Evidence(Key(id:"active"),DateTimeOffset.UtcNow.AddHours(1));
        var foreign=Evidence(Key("other","purged"),DateTimeOffset.UtcNow.AddHours(1));
        foreach(var item in new[]{expired,held,active,foreign})
            await _store.RegisterPublishedAsync(item);
        Assert.True(await _store.AcquireReferenceAsync(active.Key,"decision"));
        Assert.True(await _store.CommitExportAuditAsync(active.Key,active.OriginalSha256,AuditFor(active.Key,"RedactedExport"),default));
        Assert.Equal(RetentionResult.Removed,await _store.ExpireAsync(expired.Key,AuditFor(expired.Key),default));

        var restoredName="wowdi_sec01_restore_"+Guid.NewGuid().ToString("N");
        var directory=System.IO.Directory.CreateTempSubdirectory("wowdi-sec01-");
        var path=Path.Combine(directory.FullName,"fixture.dump");
        var created=false;
        try
        {
            await using(var admin=new NpgsqlConnection(_admin))
            {
                await admin.OpenAsync();
                await using var create=new NpgsqlCommand($"CREATE DATABASE {restoredName}",admin);
                await create.ExecuteNonQueryAsync();
                created=true;
            }
            await PgToolAsync("pg_dump",_name,["--format=custom","--no-owner","--no-privileges","--file",path]);
            Assert.True(new FileInfo(path).Length>0);
            await PgToolAsync("pg_restore",restoredName,["--exit-on-error","--no-owner","--no-privileges",path]);
            var connection=new NpgsqlConnectionStringBuilder(_admin){Database=restoredName,Pooling=false}.ConnectionString;
            var options=SecurityDb.Configure(connection);
            await using(var db=new SecurityDb(options))
            {
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.False(db.Database.HasPendingModelChanges());
            }
            await using(var db=new EnterpriseDb(EnterpriseDb.Configure(connection)))
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var restored=new PostgresEvidenceMovementStore(options);
            Assert.Null(await restored.ReadAsync(expired.Key,default));
            Assert.Equal("evidence.conflict",(await Assert.ThrowsAsync<DataSecurityException>(async()=>
                await restored.RegisterPublishedAsync(expired))).Code);
            Assert.False(await restored.AcquireReferenceAsync(expired.Key,"late"));
            Assert.Equal(RetentionResult.Protected,await restored.ExpireAsync(held.Key,AuditFor(held.Key),default));
            var loaded=Assert.IsType<StoredEvidence>(await restored.ReadAsync(active.Key,default));
            Assert.Equal(active.OriginalBytes.ToArray(),loaded.OriginalBytes.ToArray());
            Assert.Equal(active.OriginalSha256,loaded.OriginalSha256);
            Assert.Equal(1,loaded.ReferenceCount);
            Assert.NotNull(await restored.ReadAsync(foreign.Key,default));
            await using var conn=new NpgsqlConnection(connection);
            await conn.OpenAsync();
            async Task<long> CountRestored(string sql)
            {
                await using var command=new NpgsqlCommand(sql,conn);
                return (long)(await command.ExecuteScalarAsync())!;
            }
            Assert.Equal(1L,await CountRestored("SELECT count(*) FROM wowdi_security.artifacts WHERE tenant='fixture' AND evidence_id='purged' AND bytes IS NULL AND purged_at IS NOT NULL"));
            Assert.Equal(1L,await CountRestored("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='fixture' AND action='RetentionExpired'"));
            Assert.Equal(1L,await CountRestored("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='fixture' AND action='RedactedExport'"));
            Assert.Equal(0L,await CountRestored("SELECT count(*) FROM wowdi_security.movement_audit WHERE tenant='other'"));
            await using(var command=new NpgsqlCommand("DELETE FROM wowdi_security.movement_audit WHERE tenant='fixture'",conn))
                Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteNonQueryAsync())).SqlState);
            Assert.True(await restored.CommitExportAuditAsync(active.Key,active.OriginalSha256,AuditFor(active.Key,"RedactedExport"),default));
            Assert.Equal(3L,await CountRestored("SELECT count(*) FROM wowdi_security.movement_audit"));
        }
        finally
        {
            try
            {
                if(created)
                {
                    await using var admin=new NpgsqlConnection(_admin);
                    await admin.OpenAsync();
                    await using var drop=new NpgsqlCommand($"DROP DATABASE IF EXISTS {restoredName} WITH (FORCE)",admin);
                    await drop.ExecuteNonQueryAsync();
                }
            }
            finally { directory.Delete(recursive:true); }
        }
    }

    private async Task PgToolAsync(string tool,string database,string[] args)
    {
        var connection=new NpgsqlConnectionStringBuilder(_admin);
        var info=new ProcessStartInfo(tool){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var arg in new[]{"--host",connection.Host!,"--port",connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--username",connection.Username!,"--dbname",database,"--no-password"}.Concat(args))
            info.ArgumentList.Add(arg);
        info.Environment["PGPASSWORD"]=connection.Password??"";
        using var process=Process.Start(info)??throw new InvalidOperationException("Cannot start PostgreSQL client.");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if(!process.HasExited)process.Kill(entireProcessTree:true); throw; }
        await stdout;
        var diagnostic=await stderr;
        Assert.True(process.ExitCode==0,tool+" failed: "+diagnostic);
    }
}
