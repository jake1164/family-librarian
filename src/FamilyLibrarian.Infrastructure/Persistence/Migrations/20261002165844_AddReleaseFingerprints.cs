using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReleaseFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "release_fingerprint",
                schema: "requests",
                table: "request_declined_candidates",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "candidate_fingerprint",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "release_fingerprint",
                schema: "requests",
                table: "request_declined_candidates");

            migrationBuilder.DropColumn(
                name: "candidate_fingerprint",
                schema: "acquisition",
                table: "provider_acquisition_jobs");
        }
    }
}
