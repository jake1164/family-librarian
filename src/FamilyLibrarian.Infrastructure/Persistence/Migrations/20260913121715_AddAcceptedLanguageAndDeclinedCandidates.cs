using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAcceptedLanguageAndDeclinedCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accepted_language",
                schema: "requests",
                table: "request_formats",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "request_declined_candidates",
                schema: "requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_result_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    declined_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_declined_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_request_declined_candidates_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_request_declined_candidates_request_formats_request_format_~",
                        column: x => x.request_format_id,
                        principalSchema: "requests",
                        principalTable: "request_formats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_request_declined_candidates_request_format_id_provider_id_p~",
                schema: "requests",
                table: "request_declined_candidates",
                columns: new[] { "request_format_id", "provider_id", "provider_result_id" });

            migrationBuilder.CreateIndex(
                name: "IX_request_declined_candidates_request_id",
                schema: "requests",
                table: "request_declined_candidates",
                column: "request_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_declined_candidates",
                schema: "requests");

            migrationBuilder.DropColumn(
                name: "accepted_language",
                schema: "requests",
                table: "request_formats");
        }
    }
}
