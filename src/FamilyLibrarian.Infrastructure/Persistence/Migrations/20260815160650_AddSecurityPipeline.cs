using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityPipeline : Migration
    {
        // Hoisted out of the CreateIndex call to satisfy CA1861, which the build
        // treats as an error.
        private static readonly string[] SecurityEvaluationAssetCreatedIndexColumns =
            ["asset_id", "created_at_utc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "security");

            migrationBuilder.CreateTable(
                name: "security_evaluations",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_evaluations", x => x.id);
                    table.ForeignKey(
                        name: "FK_security_evaluations_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "acquisition",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    policy_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approvals", x => x.id);
                    table.ForeignKey(
                        name: "FK_approvals_security_evaluations_security_evaluation_id",
                        column: x => x.security_evaluation_id,
                        principalSchema: "security",
                        principalTable: "security_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "format_validation_results",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    validator_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_valid = table.Column<bool>(type: "boolean", nullable: false),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    validated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_format_validation_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_format_validation_results_security_evaluations_security_eva~",
                        column: x => x.security_evaluation_id,
                        principalSchema: "security",
                        principalTable: "security_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_scan_results",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scanner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    threat_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    scanner_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scanned_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_scan_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_security_scan_results_security_evaluations_security_evaluat~",
                        column: x => x.security_evaluation_id,
                        principalSchema: "security",
                        principalTable: "security_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approvals_security_evaluation_id",
                schema: "security",
                table: "approvals",
                column: "security_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "IX_format_validation_results_security_evaluation_id",
                schema: "security",
                table: "format_validation_results",
                column: "security_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_evaluations_asset_id_created_at_utc",
                schema: "security",
                table: "security_evaluations",
                columns: SecurityEvaluationAssetCreatedIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_security_scan_results_security_evaluation_id",
                schema: "security",
                table: "security_scan_results",
                column: "security_evaluation_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "security");

            migrationBuilder.DropTable(
                name: "format_validation_results",
                schema: "security");

            migrationBuilder.DropTable(
                name: "security_scan_results",
                schema: "security");

            migrationBuilder.DropTable(
                name: "security_evaluations",
                schema: "security");
        }
    }
}
