using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryUpgrades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "upgrade_scanned_at_utc",
                schema: "requests",
                table: "request_formats",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "library_upgrades",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivered_asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    candidate_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    candidate_fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reasons = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    new_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_upgrades", x => x.id);
                    table.ForeignKey(
                        name: "FK_library_upgrades_request_formats_request_format_id",
                        column: x => x.request_format_id,
                        principalSchema: "requests",
                        principalTable: "request_formats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_library_upgrades_request_format_id",
                schema: "acquisition",
                table: "library_upgrades",
                column: "request_format_id",
                unique: true,
                filter: "state IN ('Proposed','Acquiring','Publishing','Verified')");

            migrationBuilder.CreateIndex(
                name: "IX_library_upgrades_request_format_id_created_at_utc",
                schema: "acquisition",
                table: "library_upgrades",
                columns: new[] { "request_format_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "library_upgrades",
                schema: "acquisition");

            migrationBuilder.DropColumn(
                name: "upgrade_scanned_at_utc",
                schema: "requests",
                table: "request_formats");
        }
    }
}
