using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogObservationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "first_publication_basis",
                schema: "catalog",
                table: "works",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "first_publication_precision",
                schema: "catalog",
                table: "works",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "version_class",
                schema: "catalog",
                table: "works",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "binding_basis",
                schema: "catalog",
                table: "work_authors",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "binding_basis",
                schema: "catalog",
                table: "series_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "position_state",
                schema: "catalog",
                table: "series_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "identity_basis",
                schema: "catalog",
                table: "editions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "publication_precision",
                schema: "catalog",
                table: "editions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_provider_id",
                schema: "catalog",
                table: "editions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "metadata_claims",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_entity_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    value_json = table.Column<string>(type: "jsonb", nullable: false),
                    value_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    is_selected = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metadata_claims", x => x.id);
                    table.ForeignKey(
                        name: "FK_metadata_claims_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_observations",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    external_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_observations", x => x.id);
                    table.ForeignKey(
                        name: "FK_source_observations_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_claims_one_selected",
                schema: "catalog",
                table: "metadata_claims",
                columns: new[] { "work_id", "field", "scope_key" },
                unique: true,
                filter: "is_selected");

            migrationBuilder.CreateIndex(
                name: "IX_metadata_claims_work_id_field_scope_key_provider_id_source_~",
                schema: "catalog",
                table: "metadata_claims",
                columns: new[] { "work_id", "field", "scope_key", "provider_id", "source_entity_id", "value_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_observations_work_id_provider_id_external_id_content~",
                schema: "catalog",
                table: "source_observations",
                columns: new[] { "work_id", "provider_id", "external_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_observations_work_id_provider_id_external_id_observe~",
                schema: "catalog",
                table: "source_observations",
                columns: new[] { "work_id", "provider_id", "external_id", "observed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metadata_claims",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "source_observations",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "first_publication_basis",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "first_publication_precision",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "version_class",
                schema: "catalog",
                table: "works");

            migrationBuilder.DropColumn(
                name: "binding_basis",
                schema: "catalog",
                table: "work_authors");

            migrationBuilder.DropColumn(
                name: "binding_basis",
                schema: "catalog",
                table: "series_entries");

            migrationBuilder.DropColumn(
                name: "position_state",
                schema: "catalog",
                table: "series_entries");

            migrationBuilder.DropColumn(
                name: "identity_basis",
                schema: "catalog",
                table: "editions");

            migrationBuilder.DropColumn(
                name: "publication_precision",
                schema: "catalog",
                table: "editions");

            migrationBuilder.DropColumn(
                name: "source_provider_id",
                schema: "catalog",
                table: "editions");
        }
    }
}
