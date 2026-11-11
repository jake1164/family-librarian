using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tuning");

            migrationBuilder.AddColumn<int>(
                name: "capture_calls_per_snapshot",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "capture_findings_days",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<int>(
                name: "capture_labeled_snapshot_days",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "capture_snapshots_per_role",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "capture_unlabeled_snapshot_days",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 180);

            migrationBuilder.AddColumn<bool>(
                name: "chat_capture_enabled",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "decision_capture_enabled",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "decision_snapshots",
                schema: "tuning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    subject_reference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    snapshot_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_json = table.Column<string>(type: "character varying(32768)", maxLength: 32768, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    excerpts_involved = table.Column<bool>(type: "boolean", nullable: false),
                    first_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    times_seen = table.Column<int>(type: "integer", nullable: false),
                    stale_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decision_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_decision_snapshots_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_decision_snapshots_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "candidate_links",
                schema: "tuning",
                columns: table => new
                {
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_result_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_links", x => new { x.snapshot_id, x.candidate_key });
                    table.ForeignKey(
                        name: "FK_candidate_links_decision_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "tuning",
                        principalTable: "decision_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_calls",
                schema: "tuning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_usage_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    semantic_finding_id = table.Column<Guid>(type: "uuid", nullable: true),
                    settings_id = table.Column<Guid>(type: "uuid", nullable: true),
                    placement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    model_alias = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    reported_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reasoning_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    rendered_messages_hash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    parser_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    answer = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decision_calls", x => x.id);
                    table.ForeignKey(
                        name: "FK_decision_calls_decision_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "tuning",
                        principalTable: "decision_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_outcomes",
                schema: "tuning",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    snapshot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    source = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    influenced_by_model_note = table.Column<bool>(type: "boolean", nullable: false),
                    strength = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    observed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decision_outcomes", x => x.id);
                    table.ForeignKey(
                        name: "FK_decision_outcomes_decision_snapshots_snapshot_id",
                        column: x => x.snapshot_id,
                        principalSchema: "tuning",
                        principalTable: "decision_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_decision_calls_snapshot_id_created_at_utc",
                schema: "tuning",
                table: "decision_calls",
                columns: new[] { "snapshot_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_decision_outcomes_snapshot_id",
                schema: "tuning",
                table: "decision_outcomes",
                column: "snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_decision_snapshots_request_id",
                schema: "tuning",
                table: "decision_snapshots",
                column: "request_id");

            migrationBuilder.CreateIndex(
                name: "IX_decision_snapshots_role_last_seen_at_utc",
                schema: "tuning",
                table: "decision_snapshots",
                columns: new[] { "role", "last_seen_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_decision_snapshots_role_schema_version_subject_reference_sn~",
                schema: "tuning",
                table: "decision_snapshots",
                columns: new[] { "role", "schema_version", "subject_reference", "snapshot_digest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_decision_snapshots_user_id",
                schema: "tuning",
                table: "decision_snapshots",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_links",
                schema: "tuning");

            migrationBuilder.DropTable(
                name: "decision_calls",
                schema: "tuning");

            migrationBuilder.DropTable(
                name: "decision_outcomes",
                schema: "tuning");

            migrationBuilder.DropTable(
                name: "decision_snapshots",
                schema: "tuning");

            migrationBuilder.DropColumn(
                name: "capture_calls_per_snapshot",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "capture_findings_days",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "capture_labeled_snapshot_days",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "capture_snapshots_per_role",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "capture_unlabeled_snapshot_days",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "chat_capture_enabled",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "decision_capture_enabled",
                schema: "integrations",
                table: "model_gateway_settings");
        }
    }
}
