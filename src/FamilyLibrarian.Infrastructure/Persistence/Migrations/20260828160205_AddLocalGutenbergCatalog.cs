using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalGutenbergCatalog : Migration
    {
        private static readonly string[] BookGenerationIdIndexColumns = ["generation_id", "gutenberg_id"];
        private static readonly string[] BookTitleIndexColumns = ["generation_id", "normalized_title"];
        private static readonly string[] FormatIndexColumns = ["book_id", "format_kind"];
        private static readonly string[] LanguageIndexColumns = ["book_id", "language_code"];
        private static readonly string[] PersonIndexColumns = ["book_id", "sort_order"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "gutenberg");

            migrationBuilder.CreateTable(
                name: "catalog_books",
                schema: "gutenberg",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    gutenberg_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    normalized_title = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    media_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_date = table.Column<DateOnly>(type: "date", nullable: true),
                    rights_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    rights_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    download_count = table.Column<int>(type: "integer", nullable: true),
                    summary = table.Column<string>(type: "character varying(32000)", maxLength: 32000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_books", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_sync_states",
                schema: "gutenberg",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    active_generation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_attempt_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_successful_sync_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_source_modified_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_archive_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    book_count = table.Column<int>(type: "integer", nullable: false),
                    format_count = table.Column<int>(type: "integer", nullable: false),
                    parse_error_count = table.Column<int>(type: "integer", nullable: false),
                    last_duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_sync_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_formats",
                schema: "gutenberg",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    book_id = table.Column<long>(type: "bigint", nullable: false),
                    source_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    format_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    modified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_formats", x => x.id);
                    table.ForeignKey(
                        name: "FK_catalog_formats_catalog_books_book_id",
                        column: x => x.book_id,
                        principalSchema: "gutenberg",
                        principalTable: "catalog_books",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_languages",
                schema: "gutenberg",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    book_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_languages", x => x.id);
                    table.ForeignKey(
                        name: "FK_catalog_languages_catalog_books_book_id",
                        column: x => x.book_id,
                        principalSchema: "gutenberg",
                        principalTable: "catalog_books",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "catalog_people",
                schema: "gutenberg",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    book_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    birth_year = table.Column<int>(type: "integer", nullable: true),
                    death_year = table.Column<int>(type: "integer", nullable: true),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_people", x => x.id);
                    table.ForeignKey(
                        name: "FK_catalog_people_catalog_books_book_id",
                        column: x => x.book_id,
                        principalSchema: "gutenberg",
                        principalTable: "catalog_books",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_books_generation_id_gutenberg_id",
                schema: "gutenberg",
                table: "catalog_books",
                columns: BookGenerationIdIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_books_generation_id_normalized_title",
                schema: "gutenberg",
                table: "catalog_books",
                columns: BookTitleIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_formats_book_id_format_kind",
                schema: "gutenberg",
                table: "catalog_formats",
                columns: FormatIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_languages_book_id_language_code",
                schema: "gutenberg",
                table: "catalog_languages",
                columns: LanguageIndexColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_people_book_id_sort_order",
                schema: "gutenberg",
                table: "catalog_people",
                columns: PersonIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_people_normalized_name",
                schema: "gutenberg",
                table: "catalog_people",
                column: "normalized_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_formats",
                schema: "gutenberg");

            migrationBuilder.DropTable(
                name: "catalog_languages",
                schema: "gutenberg");

            migrationBuilder.DropTable(
                name: "catalog_people",
                schema: "gutenberg");

            migrationBuilder.DropTable(
                name: "catalog_sync_states",
                schema: "gutenberg");

            migrationBuilder.DropTable(
                name: "catalog_books",
                schema: "gutenberg");
        }
    }
}
