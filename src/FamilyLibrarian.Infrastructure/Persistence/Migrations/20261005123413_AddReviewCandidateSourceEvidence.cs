using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCandidateSourceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "release_name",
                schema: "requests",
                table: "request_review_candidates",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "title_is_request_fallback",
                schema: "requests",
                table: "request_review_candidates",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "release_name",
                schema: "requests",
                table: "request_review_candidates");

            migrationBuilder.DropColumn(
                name: "title_is_request_fallback",
                schema: "requests",
                table: "request_review_candidates");
        }
    }
}
