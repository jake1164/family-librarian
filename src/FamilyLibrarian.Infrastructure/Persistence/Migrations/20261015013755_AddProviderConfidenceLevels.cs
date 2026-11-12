using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderConfidenceLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audiobook_confidence_level",
                schema: "providers",
                table: "external_providers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confidence_level",
                schema: "providers",
                table: "external_providers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confidence_reviewed_at_utc",
                schema: "providers",
                table: "external_providers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ebook_confidence_level",
                schema: "providers",
                table: "external_providers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audiobook_confidence_level",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "confidence_level",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "confidence_reviewed_at_utc",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "ebook_confidence_level",
                schema: "providers",
                table: "external_providers");
        }
    }
}
