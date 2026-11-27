using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGutenbergRefreshProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "downloaded_bytes",
                schema: "gutenberg",
                table: "catalog_sync_states",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "progress_message",
                schema: "gutenberg",
                table: "catalog_sync_states",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_host",
                schema: "gutenberg",
                table: "catalog_sync_states",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "downloaded_bytes",
                schema: "gutenberg",
                table: "catalog_sync_states");

            migrationBuilder.DropColumn(
                name: "progress_message",
                schema: "gutenberg",
                table: "catalog_sync_states");

            migrationBuilder.DropColumn(
                name: "source_host",
                schema: "gutenberg",
                table: "catalog_sync_states");
        }
    }
}
