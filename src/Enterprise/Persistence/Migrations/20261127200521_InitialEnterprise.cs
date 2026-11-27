using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowDi.Enterprise.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialEnterprise : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wowdi");

            migrationBuilder.CreateTable(
                name: "content",
                schema: "wowdi",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content", x => new { x.tenant, x.hash });
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                schema: "wowdi",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model = table.Column<byte[]>(type: "bytea", nullable: false),
                    graph = table.Column<byte[]>(type: "bytea", nullable: false),
                    model_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    graph_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_revisions", x => new { x.tenant, x.model_id, x.revision_id });
                });

            migrationBuilder.CreateTable(
                name: "inputs",
                schema: "wowdi",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inputs", x => new { x.tenant, x.model_id, x.revision_id, x.hash });
                    table.ForeignKey(
                        name: "FK_inputs_content_tenant_hash",
                        columns: x => new { x.tenant, x.hash },
                        principalSchema: "wowdi",
                        principalTable: "content",
                        principalColumns: new[] { "tenant", "hash" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inputs_revisions_tenant_model_id_revision_id",
                        columns: x => new { x.tenant, x.model_id, x.revision_id },
                        principalSchema: "wowdi",
                        principalTable: "revisions",
                        principalColumns: new[] { "tenant", "model_id", "revision_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "wowdi",
                columns: table => new
                {
                    tenant = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    job_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    revision_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, collation: "C"),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    request = table.Column<byte[]>(type: "bytea", nullable: false),
                    issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, collation: "C"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => new { x.tenant, x.job_id });
                    table.ForeignKey(
                        name: "FK_jobs_revisions_tenant_model_id_revision_id",
                        columns: x => new { x.tenant, x.model_id, x.revision_id },
                        principalSchema: "wowdi",
                        principalTable: "revisions",
                        principalColumns: new[] { "tenant", "model_id", "revision_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inputs_tenant_hash",
                schema: "wowdi",
                table: "inputs",
                columns: new[] { "tenant", "hash" });

            migrationBuilder.CreateIndex(
                name: "IX_jobs_tenant_idempotency_key",
                schema: "wowdi",
                table: "jobs",
                columns: new[] { "tenant", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_jobs_tenant_model_id_revision_id",
                schema: "wowdi",
                table: "jobs",
                columns: new[] { "tenant", "model_id", "revision_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inputs",
                schema: "wowdi");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "wowdi");

            migrationBuilder.DropTable(
                name: "content",
                schema: "wowdi");

            migrationBuilder.DropTable(
                name: "revisions",
                schema: "wowdi");
        }
    }
}
