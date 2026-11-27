using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowDi.Enterprise.DataSecurity.Postgres.Migrations;

public partial class AuditedHoldTransitions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "hold_changed_at", schema: "wowdi_security", table: "artifacts",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.Sql("""
            CREATE FUNCTION wowdi_security.require_hold_audit() RETURNS trigger LANGUAGE plpgsql AS $hold$
            BEGIN
                IF NEW.legal_hold IS NOT DISTINCT FROM OLD.legal_hold THEN
                    IF NEW.hold_changed_at IS DISTINCT FROM OLD.hold_changed_at THEN
                        RAISE EXCEPTION 'hold timestamp requires a transition' USING ERRCODE='23514';
                    END IF;
                    RETURN NULL;
                END IF;
                IF NEW.hold_changed_at IS NULL OR
                   (OLD.hold_changed_at IS NOT NULL AND NEW.hold_changed_at <= OLD.hold_changed_at) OR
                   NEW.hold_changed_at < transaction_timestamp() OR
                   NEW.hold_changed_at > clock_timestamp() OR NOT EXISTS(
                    SELECT 1 FROM wowdi_security.movement_audit a
                    WHERE a.tenant=NEW.tenant AND a.model_id=NEW.model_id AND a.revision_id=NEW.revision_id
                      AND a.evidence_id=NEW.evidence_id
                      AND a.action=CASE WHEN NEW.legal_hold THEN 'HoldSet' ELSE 'HoldReleased' END
                      AND a.recorded_at=NEW.hold_changed_at
                ) THEN
                    RAISE EXCEPTION 'fresh hold transition audit is required' USING ERRCODE='23514';
                END IF;
                RETURN NULL;
            END $hold$;
            CREATE CONSTRAINT TRIGGER require_hold_audit AFTER UPDATE ON wowdi_security.artifacts
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION wowdi_security.require_hold_audit();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER require_hold_audit ON wowdi_security.artifacts;
            DROP FUNCTION wowdi_security.require_hold_audit();
            """);
        migrationBuilder.DropColumn(name: "hold_changed_at", schema: "wowdi_security", table: "artifacts");
    }
}
