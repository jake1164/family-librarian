using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchVariantAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "search_variant",
                schema: "requests",
                table: "request_review_candidates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "search_variant_attempts",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    variant_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    variant_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    first_tried_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_tried_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_result_count = table.Column<int>(type: "integer", nullable: false),
                    times_tried = table.Column<int>(type: "integer", nullable: false),
                    found_selected_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_search_variant_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_search_variant_attempts_request_formats_request_format_id",
                        column: x => x.request_format_id,
                        principalSchema: "requests",
                        principalTable: "request_formats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_search_variant_attempts_request_format_id",
                schema: "acquisition",
                table: "search_variant_attempts",
                column: "request_format_id");

            migrationBuilder.CreateIndex(
                name: "IX_search_variant_attempts_request_format_id_provider_id_varia~",
                schema: "acquisition",
                table: "search_variant_attempts",
                columns: new[] { "request_format_id", "provider_id", "variant_kind", "variant_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_variant_attempts",
                schema: "acquisition");

            migrationBuilder.DropColumn(
                name: "search_variant",
                schema: "requests",
                table: "request_review_candidates");
        }
    }
}
