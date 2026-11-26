using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultRequestFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_request_format",
                schema: "identity",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                // Existing accounts keep today's behaviour. An empty string could not be read back as a format.
                defaultValue: "Ebook");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_request_format",
                schema: "identity",
                table: "users");
        }
    }
}
