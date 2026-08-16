using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcSettingsAndBreakGlass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsBreakGlass",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "oidc_settings",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    authority = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    client_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    protected_client_secret = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    client_secret_format_version = table.Column<int>(type: "integer", nullable: false),
                    client_secret_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    client_secret_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    match_claim_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    admin_claim_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    admin_claim_values = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    auto_create_accounts = table.Column<bool>(type: "boolean", nullable: false),
                    local_login_disabled = table.Column<bool>(type: "boolean", nullable: false),
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
                    table.PrimaryKey("PK_oidc_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oidc_settings",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "IsBreakGlass",
                schema: "identity",
                table: "users");
        }
    }
}
