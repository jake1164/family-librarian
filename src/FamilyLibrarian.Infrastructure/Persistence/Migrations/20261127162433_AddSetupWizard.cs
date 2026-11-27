using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSetupWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "setup_completed_steps",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "setup_skipped_steps",
                schema: "identity",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "setup_wizard_seen_at_utc",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            // Accounts that already exist get a dismissible reminder instead of the first-sign-in wizard.
            migrationBuilder.Sql("UPDATE identity.users SET setup_wizard_seen_at_utc = now();");

            migrationBuilder.CreateTable(
                name: "setup_guidance_settings",
                schema: "communications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kindle_sender_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    amazon_store_domain = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    messaging_server_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    messaging_sign_in_instructions = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    messaging_help_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    messaging_web_app_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    show_element_help = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_guidance_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_guidance_settings",
                schema: "communications");

            migrationBuilder.DropColumn(
                name: "setup_completed_steps",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "setup_skipped_steps",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "setup_wizard_seen_at_utc",
                schema: "identity",
                table: "users");
        }
    }
}
