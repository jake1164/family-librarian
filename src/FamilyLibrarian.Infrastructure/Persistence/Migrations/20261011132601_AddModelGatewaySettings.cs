using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelGatewaySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "model_gateway_settings",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    endpoint_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    protected_api_key = table.Column<string>(type: "text", nullable: true),
                    api_key_format_version = table.Column<int>(type: "integer", nullable: false),
                    api_key_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    allow_cloud = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_gateway_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_role_settings",
                schema: "integrations",
                columns: table => new
                {
                    settings_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    placement = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    local_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    cloud_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_role_settings", x => new { x.settings_id, x.role });
                    table.ForeignKey(
                        name: "FK_model_role_settings_model_gateway_settings_settings_id",
                        column: x => x.settings_id,
                        principalSchema: "integrations",
                        principalTable: "model_gateway_settings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_role_settings",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "model_gateway_settings",
                schema: "integrations");
        }
    }
}
