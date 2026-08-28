using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations;

/// <summary>Persists observable progress for an in-flight Project Gutenberg catalogue import.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260828221000_AddGutenbergImportProgress")]
public partial class AddGutenbergImportProgress : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "in_progress_book_count",
            schema: "gutenberg",
            table: "catalog_sync_states",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "in_progress_format_count",
            schema: "gutenberg",
            table: "catalog_sync_states",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "last_progress_utc",
            schema: "gutenberg",
            table: "catalog_sync_states",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "in_progress_book_count",
            schema: "gutenberg",
            table: "catalog_sync_states");

        migrationBuilder.DropColumn(
            name: "in_progress_format_count",
            schema: "gutenberg",
            table: "catalog_sync_states");

        migrationBuilder.DropColumn(
            name: "last_progress_utc",
            schema: "gutenberg",
            table: "catalog_sync_states");
    }
}
