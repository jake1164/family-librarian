using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations;

/// <summary>Adds the data needed to apply Project Gutenberg's daily changed-books feed incrementally.</summary>
public partial class AddIncrementalGutenbergCatalogSync : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "source_fingerprint",
            schema: "gutenberg",
            table: "catalog_books",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_successful_incremental_sync_utc",
            schema: "gutenberg",
            table: "catalog_sync_states",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "source_fingerprint",
            schema: "gutenberg",
            table: "catalog_books");

        migrationBuilder.DropColumn(
            name: "last_successful_incremental_sync_utc",
            schema: "gutenberg",
            table: "catalog_sync_states");
    }
}
