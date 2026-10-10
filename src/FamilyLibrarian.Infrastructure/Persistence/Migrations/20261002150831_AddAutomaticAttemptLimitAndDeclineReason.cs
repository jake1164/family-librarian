using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticAttemptLimitAndDeclineReason : Migration
    {
        /// <inheritdoc />
        // Existing-row defaults are set deliberately rather than left at the
        // scaffolded zero/empty values. Every declined candidate that predates
        // this migration was a requester's "keep looking", and an attempt limit
        // of 0 would read as "never retry" for providers already registered,
        // silently disabling the loop on exactly the installs being upgraded.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                schema: "requests",
                table: "request_declined_candidates",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "requests",
                table: "request_declined_candidates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "RequesterDeclined");

            migrationBuilder.AddColumn<int>(
                name: "automatic_attempt_limit",
                schema: "providers",
                table: "external_providers",
                type: "integer",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failure_reason",
                schema: "requests",
                table: "request_declined_candidates");

            migrationBuilder.DropColumn(
                name: "reason",
                schema: "requests",
                table: "request_declined_candidates");

            migrationBuilder.DropColumn(
                name: "automatic_attempt_limit",
                schema: "providers",
                table: "external_providers");
        }
    }
}
