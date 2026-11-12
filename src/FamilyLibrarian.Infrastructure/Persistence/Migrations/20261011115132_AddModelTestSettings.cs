using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelTestSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.CreateTable(
                name: "model_test_settings",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    endpoint_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    protected_api_key = table.Column<string>(type: "text", nullable: true),
                    api_key_format_version = table.Column<int>(type: "integer", nullable: false),
                    api_key_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    primary_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    comparison_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cloud_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    allow_cloud = table.Column<bool>(type: "boolean", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_test_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_test_settings",
                schema: "integrations");
        }
    }
}
