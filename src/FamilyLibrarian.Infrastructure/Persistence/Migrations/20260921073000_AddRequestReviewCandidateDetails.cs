using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations;

/// <summary>
/// Persists only requester-safe edition/release facts selected from the
/// existing provider search response. Provider IDs and opaque result handles
/// remain separate server-only columns.
/// </summary>
public partial class AddRequestReviewCandidateDetails : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "details",
            schema: "requests",
            table: "request_review_candidates",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "details",
            schema: "requests",
            table: "request_review_candidates");
    }
}
