using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "catalog_audio_duration_seconds",
                schema: "catalog",
                table: "works",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "catalog_length_source",
                schema: "catalog",
                table: "works",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "catalog_page_count",
                schema: "catalog",
                table: "works",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "catalog_audio_duration_seconds",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "catalog_length_source",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "catalog_page_count",
                schema: "catalog",
                table: "works");
        }
    }
}
