using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowDi.Enterprise.DataSecurity.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialMovement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wowdi_security");

            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "wowdi_security",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    evidence_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    bytes = table.Column<byte[]>(type: "bytea", nullable: true),
                    digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    legal_hold = table.Column<bool>(type: "boolean", nullable: false),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifacts", x => new { x.tenant, x.model_id, x.revision_id, x.evidence_id });
                    table.CheckConstraint("CK_artifact_digest", "digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_artifact_size", "bytes IS NULL OR octet_length(bytes) BETWEEN 1 AND 16777216");
                    table.CheckConstraint("CK_artifact_status", "status IN ('Optimal','Feasible','Infeasible','Unbounded','Unknown')");
                    table.CheckConstraint("CK_artifact_tombstone", "(bytes IS NOT NULL AND purged_at IS NULL) OR (bytes IS NULL AND purged_at IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_artifacts_revisions_tenant_model_id_revision_id",
                        columns: x => new { x.tenant, x.model_id, x.revision_id },
                        principalSchema: "wowdi",
                        principalTable: "revisions",
                        principalColumns: new[] { "tenant", "model_id", "revision_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "artifact_references",
                schema: "wowdi_security",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    evidence_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    reference_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_references", x => new { x.tenant, x.model_id, x.revision_id, x.evidence_id, x.reference_id });
                    table.ForeignKey(
                        name: "FK_artifact_references_artifacts_tenant_model_id_revision_id_e~",
                        columns: x => new { x.tenant, x.model_id, x.revision_id, x.evidence_id },
                        principalSchema: "wowdi_security",
                        principalTable: "artifacts",
                        principalColumns: new[] { "tenant", "model_id", "revision_id", "evidence_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "movement_audit",
                schema: "wowdi_security",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    evidence_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_movement_audit", x => new { x.tenant, x.model_id, x.revision_id, x.evidence_id, x.audit_id });
                    table.CheckConstraint("CK_movement_action", "action IN ('RedactedExport','RetentionExpired','HoldSet','HoldReleased')");
                    table.ForeignKey(
                        name: "FK_movement_audit_artifacts_tenant_model_id_revision_id_eviden~",
                        columns: x => new { x.tenant, x.model_id, x.revision_id, x.evidence_id },
                        principalSchema: "wowdi_security",
                        principalTable: "artifacts",
                        principalColumns: new[] { "tenant", "model_id", "revision_id", "evidence_id" },
                        onDelete: ReferentialAction.Restrict);
                });
            // Defense in depth: even direct reference inserts must arbitrate with retention.
            migrationBuilder.Sql("""
                CREATE FUNCTION wowdi_security.guard_reference() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE available boolean;
                BEGIN
                    SELECT bytes IS NOT NULL AND expires_at > clock_timestamp()
                    INTO available FROM wowdi_security.artifacts
                    WHERE tenant=NEW.tenant AND model_id=NEW.model_id AND revision_id=NEW.revision_id
                      AND evidence_id=NEW.evidence_id FOR UPDATE;
                    IF available IS DISTINCT FROM TRUE THEN
                        RAISE EXCEPTION 'artifact is unavailable' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_reference BEFORE INSERT OR UPDATE ON wowdi_security.artifact_references
                FOR EACH ROW EXECUTE FUNCTION wowdi_security.guard_reference();

                CREATE FUNCTION wowdi_security.guard_artifact() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (NEW.tenant,NEW.model_id,NEW.revision_id,NEW.evidence_id,NEW.digest,NEW.status,NEW.expires_at)
                       IS DISTINCT FROM
                       (OLD.tenant,OLD.model_id,OLD.revision_id,OLD.evidence_id,OLD.digest,OLD.status,OLD.expires_at)
                       OR OLD.bytes IS NULL AND NEW IS DISTINCT FROM OLD
                       OR NEW.bytes IS NOT NULL AND NEW.bytes IS DISTINCT FROM OLD.bytes THEN
                        RAISE EXCEPTION 'artifact identity is immutable' USING ERRCODE='23514';
                    END IF;
                    IF OLD.bytes IS NOT NULL AND NEW.bytes IS NULL AND
                       (OLD.legal_hold OR NEW.legal_hold OR OLD.expires_at > clock_timestamp() OR EXISTS(
                           SELECT 1 FROM wowdi_security.artifact_references r
                           WHERE r.tenant=OLD.tenant AND r.model_id=OLD.model_id
                             AND r.revision_id=OLD.revision_id AND r.evidence_id=OLD.evidence_id)) THEN
                        RAISE EXCEPTION 'artifact is protected' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_artifact BEFORE UPDATE ON wowdi_security.artifacts
                FOR EACH ROW EXECUTE FUNCTION wowdi_security.guard_artifact();

                CREATE FUNCTION wowdi_security.require_purge_audit() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.bytes IS NOT NULL AND NEW.bytes IS NULL AND NOT EXISTS(
                        SELECT 1 FROM wowdi_security.movement_audit a
                        WHERE a.tenant=NEW.tenant AND a.model_id=NEW.model_id AND a.revision_id=NEW.revision_id
                          AND a.evidence_id=NEW.evidence_id AND a.action='RetentionExpired' AND a.recorded_at=NEW.purged_at) THEN
                        RAISE EXCEPTION 'retention audit is required' USING ERRCODE='23514';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER require_purge_audit AFTER UPDATE ON wowdi_security.artifacts
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION wowdi_security.require_purge_audit();

                CREATE FUNCTION wowdi_security.reject_immutable_delete() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'immutable retention record' USING ERRCODE='23514';
                END $$;
                CREATE TRIGGER protect_artifact_identity BEFORE DELETE ON wowdi_security.artifacts
                FOR EACH ROW EXECUTE FUNCTION wowdi_security.reject_immutable_delete();
                CREATE TRIGGER protect_movement_audit BEFORE UPDATE OR DELETE ON wowdi_security.movement_audit
                FOR EACH ROW EXECUTE FUNCTION wowdi_security.reject_immutable_delete();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER protect_movement_audit ON wowdi_security.movement_audit;
                DROP TRIGGER protect_artifact_identity ON wowdi_security.artifacts;
                DROP FUNCTION wowdi_security.reject_immutable_delete();
                DROP TRIGGER require_purge_audit ON wowdi_security.artifacts;
                DROP TRIGGER guard_artifact ON wowdi_security.artifacts;
                DROP TRIGGER guard_reference ON wowdi_security.artifact_references;
                DROP FUNCTION wowdi_security.require_purge_audit();
                DROP FUNCTION wowdi_security.guard_artifact();
                DROP FUNCTION wowdi_security.guard_reference();
                """);
            migrationBuilder.DropTable(
                name: "artifact_references",
                schema: "wowdi_security");

            migrationBuilder.DropTable(
                name: "movement_audit",
                schema: "wowdi_security");

            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "wowdi_security");
        }
    }
}
