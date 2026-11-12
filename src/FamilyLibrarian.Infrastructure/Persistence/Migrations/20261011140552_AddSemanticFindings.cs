using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSemanticFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "evidence");

            migrationBuilder.CreateTable(
                name: "semantic_findings",
                schema: "evidence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    schema_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    deterministic_decision = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_cloud = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verdict = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    claims_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    failure_detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_semantic_findings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_semantic_findings_kind_evidence_digest_created_at_utc",
                schema: "evidence",
                table: "semantic_findings",
                columns: new[] { "kind", "evidence_digest", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_semantic_findings_request_id",
                schema: "evidence",
                table: "semantic_findings",
                column: "request_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "semantic_findings",
                schema: "evidence");
        }
    }
}
