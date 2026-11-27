using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WowDi.Enterprise.Operations.Migrations
{
    /// <inheritdoc />
    public partial class InitialOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wowdi_ops");

            migrationBuilder.CreateTable(
                name: "denials",
                schema: "wowdi_ops",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, collation: "C"),
                    subject_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    correlation_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_denials", x => x.id);
                    table.CheckConstraint("denial_digest", "(subject_digest IS NULL OR subject_digest ~ '^[0-9a-f]{64}$') AND correlation_digest ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_denials_tenant_recorded_at",
                schema: "wowdi_ops",
                table: "denials",
                columns: new[] { "tenant", "recorded_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "denials",
                schema: "wowdi_ops");
        }
    }
}
