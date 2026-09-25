using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderInteractionAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "quiet_hours_end_minute",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quiet_hours_start_minute",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "quiet_hours_time_zone_id",
                schema: "identity",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "left_waiting_at_utc",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "waiting_since_utc",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_interaction_alerts",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    claimed_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_by_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    close_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_interaction_alerts", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_interaction_alerts_external_providers_external_pro~",
                        column: x => x.external_provider_id,
                        principalSchema: "providers",
                        principalTable: "external_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_interaction_claims",
                schema: "acquisition",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_viewer_activity_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_interaction_claims", x => x.job_id);
                    table.ForeignKey(
                        name: "FK_provider_interaction_claims_provider_acquisition_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "acquisition",
                        principalTable: "provider_acquisition_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_provider_interaction_claims_users_claimed_by_user_id",
                        column: x => x.claimed_by_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_interaction_alert_recipients",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    token_consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    token_revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    room_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    event_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    send_attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    sent_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rendered_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_interaction_alert_recipients", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_interaction_alert_recipients_provider_interaction_~",
                        column: x => x.alert_id,
                        principalSchema: "acquisition",
                        principalTable: "provider_interaction_alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_provider_interaction_alert_recipients_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_interaction_alert_recipients_alert_id",
                schema: "acquisition",
                table: "provider_interaction_alert_recipients",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_interaction_alert_recipients_token_hash",
                schema: "acquisition",
                table: "provider_interaction_alert_recipients",
                column: "token_hash",
                unique: true,
                filter: "token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_interaction_alert_recipients_user_id",
                schema: "acquisition",
                table: "provider_interaction_alert_recipients",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_interaction_alerts_external_provider_id",
                schema: "acquisition",
                table: "provider_interaction_alerts",
                column: "external_provider_id",
                unique: true,
                filter: "state IN ('Open','Claimed')");

            migrationBuilder.CreateIndex(
                name: "IX_provider_interaction_claims_claimed_by_user_id",
                schema: "acquisition",
                table: "provider_interaction_claims",
                column: "claimed_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_interaction_alert_recipients",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "provider_interaction_claims",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "provider_interaction_alerts",
                schema: "acquisition");

            migrationBuilder.DropColumn(
                name: "quiet_hours_end_minute",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "quiet_hours_start_minute",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "quiet_hours_time_zone_id",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "left_waiting_at_utc",
                schema: "acquisition",
                table: "provider_acquisition_jobs");

            migrationBuilder.DropColumn(
                name: "waiting_since_utc",
                schema: "acquisition",
                table: "provider_acquisition_jobs");
        }
    }
}
