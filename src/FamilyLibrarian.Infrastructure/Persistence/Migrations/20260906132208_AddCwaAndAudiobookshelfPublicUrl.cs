using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCwaAndAudiobookshelfPublicUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "public_url",
                schema: "publishing",
                table: "cwa_settings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_url",
                schema: "publishing",
                table: "audiobookshelf_settings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "public_url",
                schema: "publishing",
                table: "cwa_settings");

            migrationBuilder.DropColumn(
                name: "public_url",
                schema: "publishing",
                table: "audiobookshelf_settings");
        }
    }
}
