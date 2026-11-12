using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelUsageRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_usage_rollups",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_cloud = table.Column<bool>(type: "boolean", nullable: false),
                    opportunity_flags = table.Column<int>(type: "integer", nullable: false),
                    calls = table.Column<int>(type: "integer", nullable: false),
                    accepted_replies = table.Column<int>(type: "integer", nullable: false),
                    rejected_replies = table.Column<int>(type: "integer", nullable: false),
                    provider_errors = table.Column<int>(type: "integer", nullable: false),
                    cancelled_calls = table.Column<int>(type: "integer", nullable: false),
                    unclassified_calls = table.Column<int>(type: "integer", nullable: false),
                    action_calls = table.Column<int>(type: "integer", nullable: false),
                    no_change_calls = table.Column<int>(type: "integer", nullable: false),
                    abstained_calls = table.Column<int>(type: "integer", nullable: false),
                    advisory_calls = table.Column<int>(type: "integer", nullable: false),
                    reported_tokens = table.Column<long>(type: "bigint", nullable: false),
                    calls_without_usage = table.Column<int>(type: "integer", nullable: false),
                    latency_ms_sum = table.Column<long>(type: "bigint", nullable: false),
                    latency_ms_max = table.Column<long>(type: "bigint", nullable: false),
                    latency_calls = table.Column<int>(type: "integer", nullable: false),
                    latency_p95_ms = table.Column<long>(type: "bigint", nullable: false),
                    downstream_positive = table.Column<int>(type: "integer", nullable: false),
                    downstream_negative = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_usage_rollups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_usage_rollups_week_start_utc_role_model_is_cloud_oppo~",
                schema: "integrations",
                table: "model_usage_rollups",
                columns: new[] { "week_start_utc", "role", "model", "is_cloud", "opportunity_flags" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_usage_rollups",
                schema: "integrations");
        }
    }
}
