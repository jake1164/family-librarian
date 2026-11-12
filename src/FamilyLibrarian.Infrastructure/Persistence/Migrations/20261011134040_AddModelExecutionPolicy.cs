using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelExecutionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "background_installation_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 200);

            migrationBuilder.AddColumn<int>(
                name: "background_installation_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 400000);

            migrationBuilder.AddColumn<int>(
                name: "background_user_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "background_user_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "cloud_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "cloud_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "interactive_installation_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 300);

            migrationBuilder.AddColumn<int>(
                name: "interactive_installation_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 600000);

            migrationBuilder.AddColumn<int>(
                name: "interactive_user_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "interactive_user_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 60000);

            migrationBuilder.AddColumn<int>(
                name: "max_concurrent_calls",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "max_input_characters",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 12000);

            migrationBuilder.AddColumn<int>(
                name: "max_output_tokens",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 512);

            migrationBuilder.AddColumn<int>(
                name: "timeout_seconds",
                schema: "integrations",
                table: "model_gateway_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "model_usage_ledger",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    budget_class = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_cloud = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    latency_ms = table.Column<long>(type: "bigint", nullable: true),
                    reported_prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    reported_completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    reported_total_tokens = table.Column<int>(type: "integer", nullable: true),
                    charged_tokens = table.Column<int>(type: "integer", nullable: false),
                    usage_estimated = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_usage_ledger", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_usage_ledger_occurred_at_utc",
                schema: "integrations",
                table: "model_usage_ledger",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_model_usage_ledger_user_id_occurred_at_utc",
                schema: "integrations",
                table: "model_usage_ledger",
                columns: new[] { "user_id", "occurred_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_usage_ledger",
                schema: "integrations");

            migrationBuilder.DropColumn(
                name: "background_installation_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "background_installation_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "background_user_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "background_user_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "cloud_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "cloud_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "interactive_installation_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "interactive_installation_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "interactive_user_daily_requests",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "interactive_user_daily_tokens",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "max_concurrent_calls",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "max_input_characters",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "max_output_tokens",
                schema: "integrations",
                table: "model_gateway_settings");

            migrationBuilder.DropColumn(
                name: "timeout_seconds",
                schema: "integrations",
                table: "model_gateway_settings");
        }
    }
}
