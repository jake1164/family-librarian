using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates arrays for composite migration operations.

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "authors",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    sort_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    biography = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "external_references",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    observed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_references", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "series",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    description = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "works",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    normalized_title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    description = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    cover_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    first_publication_date = table.Column<DateOnly>(type: "date", nullable: true),
                    publication_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_retired = table.Column<bool>(type: "boolean", nullable: false),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_works", x => x.id);
                    table.ForeignKey(
                        name: "FK_works_works_replaced_by_id",
                        column: x => x.replaced_by_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "editions",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    publisher = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    publication_date = table.Column<DateOnly>(type: "date", nullable: true),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    isbn13 = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_editions", x => x.id);
                    table.ForeignKey(
                        name: "FK_editions_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "series_entries",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    series_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    position_sort = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_series_entries_series_series_id",
                        column: x => x.series_id,
                        principalSchema: "catalog",
                        principalTable: "series",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_series_entries_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_authors",
                schema: "catalog",
                columns: table => new
                {
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_authors", x => new { x.work_id, x.author_id });
                    table.ForeignKey(
                        name: "FK_work_authors_authors_author_id",
                        column: x => x.author_id,
                        principalSchema: "catalog",
                        principalTable: "authors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_authors_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_authors_normalized_name",
                schema: "catalog",
                table: "authors",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "IX_editions_isbn13",
                schema: "catalog",
                table: "editions",
                column: "isbn13",
                unique: true,
                filter: "isbn13 IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_editions_work_id",
                schema: "catalog",
                table: "editions",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_references_entity_type_entity_id",
                schema: "catalog",
                table: "external_references",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "IX_external_references_provider_id_entity_type_external_id",
                schema: "catalog",
                table: "external_references",
                columns: new[] { "provider_id", "entity_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_series_normalized_name",
                schema: "catalog",
                table: "series",
                column: "normalized_name");

            migrationBuilder.CreateIndex(
                name: "IX_series_entries_series_id_position_sort_position_label",
                schema: "catalog",
                table: "series_entries",
                columns: new[] { "series_id", "position_sort", "position_label" });

            migrationBuilder.CreateIndex(
                name: "IX_series_entries_series_id_work_id",
                schema: "catalog",
                table: "series_entries",
                columns: new[] { "series_id", "work_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_series_entries_work_id",
                schema: "catalog",
                table: "series_entries",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_authors_author_id",
                schema: "catalog",
                table: "work_authors",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_authors_work_id_ordinal",
                schema: "catalog",
                table: "work_authors",
                columns: new[] { "work_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_works_normalized_title",
                schema: "catalog",
                table: "works",
                column: "normalized_title");

            migrationBuilder.CreateIndex(
                name: "IX_works_replaced_by_id",
                schema: "catalog",
                table: "works",
                column: "replaced_by_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "editions",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "external_references",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "series_entries",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "work_authors",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "series",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "authors",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "works",
                schema: "catalog");
        }
    }
}

#pragma warning restore CA1861
