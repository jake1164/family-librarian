using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCwaSftpPasswordAndHostKeyTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "protected_sftp_password",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sftp_authentication_mode",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PrivateKey");

            migrationBuilder.AddColumn<string>(
                name: "sftp_host_key_fingerprint",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sftp_host_key_trusted_at_utc",
                schema: "publishing",
                table: "cwa_settings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sftp_password_format_version",
                schema: "publishing",
                table: "cwa_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "sftp_password_hint",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "sftp_password_set_at_utc",
                schema: "publishing",
                table: "cwa_settings",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "protected_sftp_password",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "sftp_authentication_mode",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "sftp_host_key_fingerprint",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "sftp_host_key_trusted_at_utc",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "sftp_password_format_version",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "sftp_password_hint",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "sftp_password_set_at_utc",
                schema: "publishing",
                table: "cwa_settings");
        }
    }
}
