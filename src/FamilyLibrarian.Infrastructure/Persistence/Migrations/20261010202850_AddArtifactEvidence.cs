using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtifactEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "artifact_evidence",
                schema: "security",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_schema_version = table.Column<int>(type: "integer", nullable: false),
                    record_kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    finding_id = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    dimension = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    polarity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    tool_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_artifact_evidence", x => x.id);
                    table.ForeignKey(
                        name: "FK_artifact_evidence_media_assets_media_asset_id",
                        column: x => x.media_asset_id,
                        principalSchema: "acquisition",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifact_evidence_media_asset_id_record_id_finding_id",
                schema: "security",
                table: "artifact_evidence",
                columns: new[] { "media_asset_id", "record_id", "finding_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_artifact_evidence_media_asset_id_recorded_at_utc",
                schema: "security",
                table: "artifact_evidence",
                columns: new[] { "media_asset_id", "recorded_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "artifact_evidence",
                schema: "security");
        }
    }
}
