using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCwaEreaderServiceAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ereader_service_account_password_format_version",
                schema: "publishing",
                table: "cwa_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ereader_service_account_password_hint",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ereader_service_account_password_set_at_utc",
                schema: "publishing",
                table: "cwa_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ereader_service_account_username",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "protected_ereader_service_account_password",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ereader_service_account_password_format_version",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "ereader_service_account_password_hint",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "ereader_service_account_password_set_at_utc",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "ereader_service_account_username",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "protected_ereader_service_account_password",
                schema: "publishing",
                table: "cwa_settings");
        }
    }
}
