using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace WowDi.Enterprise.DataSecurity.Postgres.Tests;

public sealed partial class MovementPostgresTests
{
    [Fact]
    public async Task HoldTransitionRequiresFreshScopedAuditAndRollsBackOnFailure()
    {
        var held=Evidence(Key(),DateTimeOffset.UtcNow.AddHours(-1),true);
        await _store.RegisterPublishedAsync(held);
        var rejected=await Assert.ThrowsAsync<PostgresException>(()=>Execute(
            "UPDATE wowdi_security.artifacts SET legal_hold=false WHERE tenant='fixture' AND evidence_id='e1'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation,rejected.SqlState);
        Assert.True((await _store.ReadAsync(held.Key,default))!.LegalHold);
        await Execute("""
            CREATE FUNCTION wowdi_security.fail_hold() RETURNS trigger LANGUAGE plpgsql AS $fixture$
            BEGIN RAISE EXCEPTION 'fixture audit failure'; END $fixture$;
            CREATE TRIGGER fail_hold BEFORE INSERT ON wowdi_security.movement_audit
            FOR EACH ROW EXECUTE FUNCTION wowdi_security.fail_hold();
            """);
        await Assert.ThrowsAsync<DbUpdateException>(async()=>await _store.SetLegalHoldAsync(held.Key,false,AuditFor(held.Key,"HoldReleased")));
        Assert.True((await _store.ReadAsync(held.Key,default))!.LegalHold);
        Assert.Equal(0L,await Count("SELECT count(*) FROM wowdi_security.movement_audit"));
        await Execute("DROP TRIGGER fail_hold ON wowdi_security.movement_audit");
        Assert.True(await _store.SetLegalHoldAsync(held.Key,false,AuditFor(held.Key,"HoldReleased")));
        Assert.True(await _store.SetLegalHoldAsync(held.Key,false,AuditFor(held.Key,"HoldReleased")));
        Assert.True(await _store.SetLegalHoldAsync(held.Key,true,AuditFor(held.Key,"HoldSet")));
        var replay=await Assert.ThrowsAsync<PostgresException>(()=>Execute("""
            UPDATE wowdi_security.artifacts a SET legal_hold=false,
            hold_changed_at=(SELECT recorded_at FROM wowdi_security.movement_audit
                WHERE tenant=a.tenant AND model_id=a.model_id AND revision_id=a.revision_id
                  AND evidence_id=a.evidence_id AND action='HoldReleased')
            WHERE tenant='fixture' AND evidence_id='e1'
            """));
        Assert.Equal(PostgresErrorCodes.CheckViolation,replay.SqlState);
        Assert.True((await _store.ReadAsync(held.Key,default))!.LegalHold);
        Assert.Equal(2L,await Count("SELECT count(*) FROM wowdi_security.movement_audit"));
        Assert.Equal(RetentionResult.Protected,await _store.ExpireAsync(held.Key,AuditFor(held.Key),default));
        Assert.True(await _store.SetLegalHoldAsync(held.Key,false,AuditFor(held.Key,"HoldReleased")));
        Assert.Equal(RetentionResult.Removed,await _store.ExpireAsync(held.Key,AuditFor(held.Key),default));
        Assert.Equal(4L,await Count("SELECT count(*) FROM wowdi_security.movement_audit"));
    }
}
