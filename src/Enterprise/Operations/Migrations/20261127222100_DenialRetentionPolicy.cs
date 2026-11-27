using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowDi.Enterprise.Operations.Migrations
{
    /// <inheritdoc />
    public partial class DenialRetentionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE wowdi_ops.denial_retention_policy(
                    tenant varchar(512) COLLATE "C",
                    minimum_age_seconds integer NOT NULL CHECK(minimum_age_seconds > 0),
                    legal_hold boolean NOT NULL,
                    version bigint NOT NULL CHECK(version > 0),
                    UNIQUE NULLS NOT DISTINCT(tenant)
                );
                ALTER TABLE wowdi_ops.denial_retention_receipts ADD COLUMN policy_version bigint CHECK(policy_version > 0);
                CREATE OR REPLACE FUNCTION wowdi_ops.expire_denials(op uuid,scope_tenant varchar,cutoff_at timestamptz,max_rows integer)
                RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $retention$
                DECLARE old wowdi_ops.denial_retention_receipts%ROWTYPE; removed_count integer; policy wowdi_ops.denial_retention_policy%ROWTYPE;
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
                    SELECT * INTO policy FROM wowdi_ops.denial_retention_policy WHERE tenant IS NOT DISTINCT FROM scope_tenant FOR SHARE;
                    IF NOT FOUND OR policy.legal_hold OR
                       cutoff_at > clock_timestamp() - make_interval(secs => policy.minimum_age_seconds) THEN
                        RAISE EXCEPTION 'retention policy denied' USING ERRCODE='23514';
                    END IF;
                    WITH picked AS (
                        SELECT id FROM wowdi_ops.denials WHERE tenant IS NOT DISTINCT FROM scope_tenant AND recorded_at < cutoff_at
                        ORDER BY recorded_at,id LIMIT max_rows FOR UPDATE SKIP LOCKED
                    ), gone AS (
                        DELETE FROM wowdi_ops.denials d USING picked p WHERE d.id=p.id RETURNING d.id
                    ) SELECT count(*) INTO removed_count FROM gone;
                    INSERT INTO wowdi_ops.denial_retention_receipts(operation_id,tenant,cutoff,batch_limit,removed,policy_version)
                        VALUES(op,scope_tenant,cutoff_at,max_rows,removed_count,policy.version);
                    RETURN removed_count;
                END $retention$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION wowdi_ops.expire_denials(op uuid,scope_tenant varchar,cutoff_at timestamptz,max_rows integer)
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
                ALTER TABLE wowdi_ops.denial_retention_receipts DROP COLUMN policy_version;
                DROP TABLE wowdi_ops.denial_retention_policy;
                """);
        }
    }
}
