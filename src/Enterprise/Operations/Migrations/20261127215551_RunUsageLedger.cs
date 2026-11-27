using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowDi.Enterprise.Operations.Migrations
{
    /// <inheritdoc />
    public partial class RunUsageLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_usage",
                schema: "wowdi_ops",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    job_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    epoch = table.Column<Guid>(type: "uuid", nullable: false),
                    fence = table.Column<long>(type: "bigint", nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    input_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    threads = table.Column<int>(type: "integer", nullable: false),
                    memory_budget_bytes = table.Column<long>(type: "bigint", nullable: false),
                    elapsed_ms = table.Column<long>(type: "bigint", nullable: false),
                    reserved_core_ms = table.Column<long>(type: "bigint", nullable: false),
                    cpu_ms = table.Column<long>(type: "bigint", nullable: true),
                    scope_peak_bytes = table.Column<long>(type: "bigint", nullable: true),
                    scope_peak_metric = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    termination = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_usage", x => new { x.tenant, x.job_id, x.epoch, x.fence });
                    table.CheckConstraint("usage_values", "fence > 0 AND threads > 0 AND memory_budget_bytes > 0 AND elapsed_ms >= 0 AND reserved_core_ms >= 0 AND reserved_core_ms::numeric = elapsed_ms::numeric * threads AND (cpu_ms IS NULL OR cpu_ms >= 0) AND (scope_peak_bytes IS NULL OR scope_peak_bytes >= 0) AND ((scope_peak_bytes IS NULL AND scope_peak_metric IS NULL) OR (scope_peak_bytes IS NOT NULL AND scope_peak_metric IS NOT NULL AND scope_peak_metric IN ('cgroup-v2-charged-memory','windows-job-commit'))) AND input_sha256 ~ '^[0-9a-f]{64}$' AND termination IN ('Completed','Failed','Cancelled','Timeout')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_usage_tenant_model_id_recorded_at",
                schema: "wowdi_ops",
                table: "run_usage",
                columns: new[] { "tenant", "model_id", "recorded_at" });
            migrationBuilder.Sql("""
                ALTER TABLE wowdi_ops.run_usage ADD COLUMN trace_id varchar(32)
                    CHECK (trace_id IS NULL OR trace_id ~ '^[0-9a-f]{32}$');
                ALTER TABLE wowdi_ops.run_usage ADD CONSTRAINT usage_job
                    FOREIGN KEY (tenant,job_id) REFERENCES wowdi.jobs(tenant,job_id) ON DELETE RESTRICT;
                ALTER TABLE wowdi_ops.run_usage ADD CONSTRAINT usage_revision
                    FOREIGN KEY (tenant,model_id,revision_id) REFERENCES wowdi.revisions(tenant,model_id,revision_id) ON DELETE RESTRICT;
                CREATE FUNCTION wowdi_ops.check_usage_scope() RETURNS trigger LANGUAGE plpgsql AS $scope$
                BEGIN
                    IF NOT EXISTS(SELECT 1 FROM wowdi.jobs j WHERE j.tenant=NEW.tenant
                        AND j.job_id=NEW.job_id AND j.model_id=NEW.model_id AND j.revision_id=NEW.revision_id) THEN
                        RAISE EXCEPTION 'usage scope mismatch' USING ERRCODE='23514';
                    END IF;
                    RETURN NEW;
                END $scope$;
                CREATE TRIGGER check_usage_scope BEFORE INSERT ON wowdi_ops.run_usage
                    FOR EACH ROW EXECUTE FUNCTION wowdi_ops.check_usage_scope();
                CREATE FUNCTION wowdi_ops.reject_mutation() RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN RAISE EXCEPTION 'immutable operations record' USING ERRCODE='23514'; END $guard$;
                CREATE TRIGGER protect_usage BEFORE UPDATE OR DELETE ON wowdi_ops.run_usage
                    FOR EACH ROW EXECUTE FUNCTION wowdi_ops.reject_mutation();
                CREATE TRIGGER protect_denials BEFORE UPDATE ON wowdi_ops.denials
                    FOR EACH ROW EXECUTE FUNCTION wowdi_ops.reject_mutation();
                CREATE TABLE wowdi_ops.denial_retention_receipts(
                    operation_id uuid PRIMARY KEY,
                    tenant varchar(512) COLLATE "C",
                    cutoff timestamptz NOT NULL,
                    batch_limit integer NOT NULL CHECK(batch_limit BETWEEN 1 AND 1000),
                    removed integer NOT NULL CHECK(removed >= 0 AND removed <= batch_limit),
                    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
                );
                CREATE TRIGGER protect_retention_receipts BEFORE UPDATE OR DELETE ON wowdi_ops.denial_retention_receipts
                    FOR EACH ROW EXECUTE FUNCTION wowdi_ops.reject_mutation();
                CREATE FUNCTION wowdi_ops.expire_denials(op uuid,scope_tenant varchar,cutoff_at timestamptz,max_rows integer)
                RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $retention$
                DECLARE old wowdi_ops.denial_retention_receipts%ROWTYPE; removed_count integer;
                BEGIN
                    IF op IS NULL OR op='00000000-0000-0000-0000-000000000000'::uuid OR cutoff_at IS NULL OR
                        NOT isfinite(cutoff_at) OR cutoff_at > clock_timestamp() OR max_rows IS NULL OR
                        max_rows NOT BETWEEN 1 AND 1000 OR (scope_tenant IS NOT NULL AND
                        (length(scope_tenant) NOT BETWEEN 1 AND 512 OR btrim(scope_tenant)<>scope_tenant)) THEN
                        RAISE EXCEPTION 'invalid retention request' USING ERRCODE='22023';
                    END IF;
                    PERFORM pg_advisory_xact_lock(hashtextextended(op::text,0));
                    SELECT * INTO old FROM wowdi_ops.denial_retention_receipts WHERE operation_id=op;
                    IF FOUND THEN
                        IF old.tenant IS DISTINCT FROM scope_tenant OR old.cutoff<>cutoff_at OR old.batch_limit<>max_rows THEN
                            RAISE EXCEPTION 'retention retry conflict' USING ERRCODE='23514';
                        END IF;
                        RETURN old.removed;
                    END IF;
                    WITH picked AS (
                        SELECT id FROM wowdi_ops.denials WHERE tenant IS NOT DISTINCT FROM scope_tenant AND recorded_at < cutoff_at
                        ORDER BY recorded_at,id LIMIT max_rows FOR UPDATE SKIP LOCKED
                    ), gone AS (
                        DELETE FROM wowdi_ops.denials d USING picked p WHERE d.id=p.id RETURNING d.id
                    ) SELECT count(*) INTO removed_count FROM gone;
                    INSERT INTO wowdi_ops.denial_retention_receipts(operation_id,tenant,cutoff,batch_limit,removed)
                        VALUES(op,scope_tenant,cutoff_at,max_rows,removed_count);
                    RETURN removed_count;
                END $retention$;
                REVOKE ALL ON FUNCTION wowdi_ops.expire_denials(uuid,varchar,timestamptz,integer) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION wowdi_ops.expire_denials(uuid,varchar,timestamptz,integer);
                DROP TABLE wowdi_ops.denial_retention_receipts;
                DROP TRIGGER protect_denials ON wowdi_ops.denials;
                """);
            migrationBuilder.DropTable(
                name: "run_usage",
                schema: "wowdi_ops");
            migrationBuilder.Sql("DROP FUNCTION wowdi_ops.check_usage_scope(); DROP FUNCTION wowdi_ops.reject_mutation();");
        }
    }
}
