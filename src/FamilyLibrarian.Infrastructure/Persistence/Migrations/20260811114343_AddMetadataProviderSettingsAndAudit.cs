using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataProviderSettingsAndAudit : Migration
    {
        // Hoisted out of the CreateIndex call to satisfy CA1861, which the build
        // treats as an error.
        private static readonly string[] AuditSubjectIndexColumns = ["subject_type", "subject_id"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    subject_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "metadata_provider_settings",
                schema: "app",
                columns: table => new
                {
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    protected_credential = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    credential_format_version = table.Column<int>(type: "integer", nullable: false),
                    credential_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    credential_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_provider_settings", x => x.provider_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_occurred_at_utc",
                schema: "audit",
                table: "audit_events",
                column: "occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_subject_type_subject_id",
                schema: "audit",
                table: "audit_events",
                columns: AuditSubjectIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "metadata_provider_settings",
                schema: "app");
        }
    }
}
