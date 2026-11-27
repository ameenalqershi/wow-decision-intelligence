using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WowDi.Enterprise.Identity;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using Xunit;

namespace WowDi.Enterprise.Operations.Tests;

public sealed partial class DenialTests
{
    private async Task Execute(string sql)
    {
        await using var conn=new NpgsqlConnection(_connection);await conn.OpenAsync();
        await using var cmd=new NpgsqlCommand(sql,conn);await cmd.ExecuteNonQueryAsync();
    }
    private async Task<long> Count(string sql)
    {
        await using var conn=new NpgsqlConnection(_connection);await conn.OpenAsync();
        await using var cmd=new NpgsqlCommand(sql,conn);return (long)(await cmd.ExecuteScalarAsync())!;
    }
    private async Task SeedJobs()
    {
        // Minimal relational anchors only. E02 actual revision/job admission separately tested.
        await Execute("""
            INSERT INTO wowdi.revisions(tenant,model_id,revision_id,model,graph,model_hash,graph_hash)
            VALUES ('tenant-a','model','r1',decode('00','hex'),decode('00','hex'),repeat('0',64),repeat('0',64)),
                   ('tenant-b','model','r1',decode('00','hex'),decode('00','hex'),repeat('0',64),repeat('0',64)),
                   ('tenant-a','model','r2',decode('00','hex'),decode('00','hex'),repeat('0',64),repeat('0',64));
            INSERT INTO wowdi.jobs(tenant,job_id,model_id,revision_id,idempotency_key,request_hash,request,issuer,subject,created_at,state)
            VALUES ('tenant-a','job','model','r1','key',repeat('0',64),decode('00','hex'),'fixture','subject',clock_timestamp(),'Queued'),
                   ('tenant-b','job','model','r1','key',repeat('0',64),decode('00','hex'),'fixture','subject',clock_timestamp(),'Queued');
            """);
    }
    [Fact]
    public async Task UsageLedgerIsScopedIdempotentAndTraceContainsNoIdentifiers()
    {
        await SeedJobs();
        var recorder=new RunUsageRecorder(OperationsDb.Configure(_connection));
        var seen=new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener=new ActivityListener
        {
            ShouldListenTo=s=>s.Name=="WowDi.Enterprise.Operations",
            Sample=(ref ActivityCreationOptions<ActivityContext> _)=>ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped=a=>seen.Add(a)
        };
        ActivitySource.AddActivityListener(listener);
        var epoch=Guid.NewGuid();
        WorkerLease Lease(string tenant,string revision="r1",long fence=1)=>new(
            new(new(tenant,"model",revision,"job"),new(2,1024*1024,TimeSpan.FromSeconds(30)),new string('a',64)),epoch,fence,TimeSpan.FromSeconds(20));
        // Synthetic measured-result fixtures; S07 actual OS accounting tests are independent.
        var result=new BoundaryResult(Termination.Completed,0,[1],new(100,75,null),true,null,1024,"cgroup-v2-charged-memory",200);
        await Task.WhenAll(Enumerable.Range(0,24).Select(i=>recorder.RecordAsync(Lease(i%2==0?"tenant-a":"tenant-b"),result)));
        await recorder.RecordAsync(Lease("tenant-a",fence:2),result with{Termination=Termination.Failed,Candidate=null,Resources=new(100,null,null)});
        Assert.Equal(3L,await Count("SELECT count(*) FROM wowdi_ops.run_usage"));
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_ops.run_usage WHERE tenant='tenant-a'"));
        Assert.Equal(3L,await Count("SELECT count(*) FROM wowdi_ops.run_usage WHERE trace_id IS NOT NULL"));
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_ops.run_usage WHERE cpu_ms=75"));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.run_usage WHERE cpu_ms IS NULL"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>recorder.RecordAsync(Lease("tenant-a"),result with{Resources=new(100,76,null)}));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>recorder.RecordAsync(Lease("tenant-a","r2",3),result));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>recorder.RecordAsync(Lease("foreign"),result));
        Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>Execute("UPDATE wowdi_ops.run_usage SET cpu_ms=0"))).SqlState);
        Assert.NotEmpty(seen);
        Assert.All(seen,a=>Assert.All(a.TagObjects,t=>Assert.Contains(t.Key,new[]{"worker.termination","worker.cpu.measured","worker.memory.measured"})));
    }

    [Fact]
    public async Task RetentionIsBoundedScopedIdempotentAndRollsBackOnReceiptFailure()
    {
        var options=OperationsDb.Configure(_connection);
        await Execute("""
            INSERT INTO wowdi_ops.denials(id,tenant,subject_digest,correlation_digest,action,recorded_at)
            SELECT gen_random_uuid(),tenant,NULL,repeat('a',64),'View',clock_timestamp()-age
            FROM (VALUES ('tenant-a',interval '40 days'),('tenant-a',interval '40 days'),
                         ('tenant-a',interval '40 days'),('tenant-b',interval '40 days'),
                         (NULL,interval '40 days'),('tenant-a',interval '1 day')) AS fixture(tenant,age);
            """);
        var retention=new DenialRetention(options);
        var cutoff=DateTimeOffset.UtcNow.AddDays(-30);
        var id=Guid.NewGuid();
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>retention.ExpireBatchAsync(id,"tenant-a",cutoff,2)));
        Assert.All(results,x=>Assert.Equal(2,x));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denial_retention_receipts"));
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-a'"));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-b'"));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant IS NULL"));
        await Assert.ThrowsAsync<PostgresException>(()=>retention.ExpireBatchAsync(id,"tenant-b",cutoff,2));
        await Execute("""
            CREATE FUNCTION wowdi_ops.fixture_fail() RETURNS trigger LANGUAGE plpgsql AS $fixture$
            BEGIN RAISE EXCEPTION 'fixture receipt failure'; END $fixture$;
            CREATE TRIGGER fixture_fail BEFORE INSERT ON wowdi_ops.denial_retention_receipts
            FOR EACH ROW EXECUTE FUNCTION wowdi_ops.fixture_fail();
            """);
        await Assert.ThrowsAsync<PostgresException>(()=>retention.ExpireBatchAsync(Guid.NewGuid(),"tenant-a",cutoff,100));
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-a'"));
        await Execute("DROP TRIGGER fixture_fail ON wowdi_ops.denial_retention_receipts");
        Assert.Equal(1,await retention.ExpireBatchAsync(Guid.NewGuid(),"tenant-a",cutoff,100));
        Assert.Equal(1,await retention.ExpireBatchAsync(Guid.NewGuid(),null,cutoff,100));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-a'"));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-b'"));
        Assert.Equal(3L,await Count("SELECT count(*) FROM wowdi_ops.denial_retention_receipts"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,(await Assert.ThrowsAsync<PostgresException>(()=>Execute("DELETE FROM wowdi_ops.denial_retention_receipts"))).SqlState);
    }
}
