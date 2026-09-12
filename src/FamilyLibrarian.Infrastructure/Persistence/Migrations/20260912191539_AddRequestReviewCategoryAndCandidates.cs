using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestReviewCategoryAndCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "review_category",
                schema: "requests",
                table: "book_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "request_review_candidates",
                schema: "requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_result_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_review_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_request_review_candidates_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_request_review_candidates_request_formats_request_format_id",
                        column: x => x.request_format_id,
                        principalSchema: "requests",
                        principalTable: "request_formats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_request_review_candidates_request_format_id",
                schema: "requests",
                table: "request_review_candidates",
                column: "request_format_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_review_candidates_request_id_display_order",
                schema: "requests",
                table: "request_review_candidates",
                columns: new[] { "request_id", "display_order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_review_candidates",
                schema: "requests");

            migrationBuilder.DropColumn(
                name: "review_category",
                schema: "requests",
                table: "book_requests");
        }
    }
}
