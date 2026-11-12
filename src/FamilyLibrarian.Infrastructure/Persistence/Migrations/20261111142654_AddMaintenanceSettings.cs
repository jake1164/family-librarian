using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_settings",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rejected_days = table.Column<int>(type: "integer", nullable: false),
                    ruled_out_days = table.Column<int>(type: "integer", nullable: false),
                    waiting_ebook_days = table.Column<int>(type: "integer", nullable: false),
                    waiting_audiobook_days = table.Column<int>(type: "integer", nullable: false),
                    stuck_days = table.Column<int>(type: "integer", nullable: false),
                    held_cap_gb = table.Column<int>(type: "integer", nullable: false),
                    orphan_grace_hours = table.Column<int>(type: "integer", nullable: false),
                    pressure_days = table.Column<int>(type: "integer", nullable: false),
                    evidence_days = table.Column<int>(type: "integer", nullable: false),
                    semantic_finding_days = table.Column<int>(type: "integer", nullable: false),
                    attempt_days = table.Column<int>(type: "integer", nullable: false),
                    search_memory_days = table.Column<int>(type: "integer", nullable: false),
                    notification_days = table.Column<int>(type: "integer", nullable: false),
                    alert_days = table.Column<int>(type: "integer", nullable: false),
                    communication_days = table.Column<int>(type: "integer", nullable: false),
                    audit_days = table.Column<int>(type: "integer", nullable: false),
                    upgrade_scan_interval_days = table.Column<int>(type: "integer", nullable: false),
                    storage_last_run_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    storage_last_run_removed = table.Column<int>(type: "integer", nullable: false),
                    storage_last_run_failures = table.Column<int>(type: "integer", nullable: false),
                    database_last_run_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    database_last_run_removed = table.Column<int>(type: "integer", nullable: false),
                    database_last_run_failures = table.Column<int>(type: "integer", nullable: false),
                    upgrade_last_run_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    upgrade_last_run_proposed = table.Column<int>(type: "integer", nullable: false),
                    upgrade_last_run_failures = table.Column<int>(type: "integer", nullable: false),
                    customized = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_settings",
                schema: "acquisition");
        }
    }
}
