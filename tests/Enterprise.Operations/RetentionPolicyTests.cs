using Npgsql;
using Xunit;

namespace WowDi.Enterprise.Operations.Tests;

public sealed partial class DenialTests
{
    private Task SeedRetentionPolicies()=>Execute("""
        INSERT INTO wowdi_ops.denial_retention_policy(tenant,minimum_age_seconds,legal_hold,version)
        VALUES ('tenant-a',2592000,false,1),('tenant-b',2592000,false,1),(NULL,2592000,false,1);
        """);

    [Fact]
    public async Task RetentionPolicyFailsClosedAndSerializesWithLegalHold()
    {
        var cutoff=DateTimeOffset.UtcNow.AddDays(-31);
        var retention=new DenialRetention(OperationsDb.Configure(_connection));
        await Execute("""
            INSERT INTO wowdi_ops.denials(id,tenant,correlation_digest,action,recorded_at)
            VALUES (gen_random_uuid(),'tenant-a',repeat('a',64),'View',clock_timestamp()-interval '40 days'),
                   (gen_random_uuid(),'tenant-b',repeat('b',64),'View',clock_timestamp()-interval '40 days');
            """);
        var missing=await Assert.ThrowsAsync<PostgresException>(()=>retention.ExpireBatchAsync(Guid.NewGuid(),"tenant-a",cutoff,10));
        Assert.Equal("23514",missing.SqlState);
        await SeedRetentionPolicies();
        var recent=await Assert.ThrowsAsync<PostgresException>(()=>retention.ExpireBatchAsync(Guid.NewGuid(),"tenant-a",DateTimeOffset.UtcNow.AddDays(-1),10));
        Assert.Equal("23514",recent.SqlState);
        await using(var connection=new NpgsqlConnection(_connection))
        {
            await connection.OpenAsync();
            await using var tx=await connection.BeginTransactionAsync();
            await using var update=new NpgsqlCommand("UPDATE wowdi_ops.denial_retention_policy SET legal_hold=true,version=2 WHERE tenant='tenant-a'",connection,tx);
            Assert.Equal(1,await update.ExecuteNonQueryAsync());
            var constrained=new NpgsqlConnectionStringBuilder(_connection){Options="-c lock_timeout=500ms"}.ConnectionString;
            var blocked=new DenialRetention(OperationsDb.Configure(constrained));
            var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>blocked.ExpireBatchAsync(Guid.NewGuid(),"tenant-a",cutoff,10));
            Assert.Equal("55P03",Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            await tx.CommitAsync();
        }
        var held=await Assert.ThrowsAsync<PostgresException>(()=>retention.ExpireBatchAsync(Guid.NewGuid(),"tenant-a",cutoff,10));
        Assert.Equal("23514",held.SqlState);
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_ops.denials"));
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_ops.denial_retention_receipts"));
        var op=Guid.NewGuid();
        Assert.Equal(1,await retention.ExpireBatchAsync(op,"tenant-b",cutoff,10));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denial_retention_receipts WHERE tenant='tenant-b' AND policy_version=1"));
        await Execute("UPDATE wowdi_ops.denial_retention_policy SET legal_hold=true,version=2 WHERE tenant='tenant-b'");
        // A retry merely returns the committed receipt; it does not delete anything.
        Assert.Equal(1,await retention.ExpireBatchAsync(op,"tenant-b",cutoff,10));
        Assert.Equal(1L,await Count("SELECT count(*) FROM wowdi_ops.denials WHERE tenant='tenant-a'"));
    }
}
