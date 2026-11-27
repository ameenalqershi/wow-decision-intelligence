using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WowDi.Enterprise.Persistence;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using Xunit;

namespace WowDi.Enterprise.Operations.Tests;

public sealed partial class DenialTests
{
    [Fact]
    public async Task LogicalRestorePreservesRetentionReceiptsAndUsageDeduplication()
    {
        await SeedJobs();
        var recorder=new RunUsageRecorder(OperationsDb.Configure(_connection));
        var lease=new WorkerLease(new(new("tenant-a","model","r1","job"),new(1,1048576,TimeSpan.FromSeconds(30)),new string('b',64)),Guid.NewGuid(),1,TimeSpan.FromSeconds(20));
        var result=new BoundaryResult(Termination.Completed,0,[1],new(100,75,null),true,null,2048,"cgroup-v2-charged-memory",100);
        await recorder.RecordAsync(lease,result);
        await Execute("""
            INSERT INTO wowdi_ops.denials(id,tenant,subject_digest,correlation_digest,action,recorded_at)
            VALUES (gen_random_uuid(),'tenant-a',NULL,repeat('a',64),'View',clock_timestamp()-interval '40 days'),
                   (gen_random_uuid(),'tenant-b',NULL,repeat('b',64),'View',clock_timestamp()-interval '40 days')
            """);
        await SeedRetentionPolicies();
        var operation=Guid.NewGuid();
        var cutoff=DateTimeOffset.UtcNow.AddDays(-30);
        Assert.Equal(1,await new DenialRetention(OperationsDb.Configure(_connection)).ExpireBatchAsync(operation,"tenant-a",cutoff,10));
        var name="wowdi_ops_restore_"+Guid.NewGuid().ToString("N");
        var directory=Directory.CreateTempSubdirectory("wowdi-ops-");
        var file=Path.Combine(directory.FullName,"fixture.dump");
        var created=false;
        try
        {
            await using(var conn=new NpgsqlConnection(_admin))
            {
                await conn.OpenAsync();
                await using var cmd=new NpgsqlCommand($"CREATE DATABASE {name}",conn);
                await cmd.ExecuteNonQueryAsync();
                created=true;
            }
            await PgTool("pg_dump",_db,["--format=custom","--no-owner","--no-privileges","--file",file]);
            Assert.True(new FileInfo(file).Length>0);
            await PgTool("pg_restore",name,["--exit-on-error","--no-owner","--no-privileges",file]);
            var connection=new NpgsqlConnectionStringBuilder(_admin){Database=name,Pooling=false}.ConnectionString;
            var options=OperationsDb.Configure(connection);
            await using(var db=new OperationsDb(options))
            {
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
            await using(var db=new EnterpriseDb(EnterpriseDb.Configure(connection)))Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            await new RunUsageRecorder(options).RecordAsync(lease,result);
            Assert.Equal(1,await new DenialRetention(options).ExpireBatchAsync(operation,"tenant-a",cutoff,10));
            await using var c=new NpgsqlConnection(connection);await c.OpenAsync();
            async Task<long> CountRestored(string sql)
            {await using var command=new NpgsqlCommand(sql,c);return (long)(await command.ExecuteScalarAsync())!;}
            Assert.Equal(1L,await CountRestored("SELECT count(*) FROM wowdi_ops.run_usage"));
            Assert.Equal(1L,await CountRestored("SELECT count(*) FROM wowdi_ops.denial_retention_receipts"));
            Assert.Equal(0L,await CountRestored("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-a'"));
            Assert.Equal(1L,await CountRestored("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-b'"));
        }
        finally
        {
            try
            {
                if(created)
                {
                    await using var conn=new NpgsqlConnection(_admin);await conn.OpenAsync();
                    await using var command=new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)",conn);
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally{directory.Delete(recursive:true);}
        }
    }
    private async Task PgTool(string executable,string database,string[] arguments)
    {
        var settings=new NpgsqlConnectionStringBuilder(_admin);
        var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--host",settings.Host!,"--port",settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--username",settings.Username!,"--dbname",database,"--no-password"}.Concat(arguments))info.ArgumentList.Add(arg);
        info.Environment["PGPASSWORD"]=settings.Password??"";
        using var process=Process.Start(info)??throw new InvalidOperationException("PostgreSQL client unavailable.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try{await process.WaitForExitAsync(timeout.Token);}
        catch{if(!process.HasExited)process.Kill(entireProcessTree:true);throw;}
        await stdout;
        Assert.True(process.ExitCode==0,executable+" failed: "+await stderr);
    }
}
