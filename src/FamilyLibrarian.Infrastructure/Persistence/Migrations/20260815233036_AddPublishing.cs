using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublishing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "publishing");

            migrationBuilder.CreateTable(
                name: "audiobookshelf_settings",
                schema: "publishing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    library_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    folder_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    protected_api_token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    api_token_format_version = table.Column<int>(type: "integer", nullable: false),
                    api_token_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    api_token_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audiobookshelf_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cwa_settings",
                schema: "publishing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    transport_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    local_ingest_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    sftp_host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sftp_port = table.Column<int>(type: "integer", nullable: true),
                    sftp_username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    sftp_ingest_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    protected_sftp_private_key = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    sftp_private_key_format_version = table.Column<int>(type: "integer", nullable: false),
                    sftp_private_key_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    sftp_private_key_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    protected_sftp_passphrase = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    sftp_passphrase_format_version = table.Column<int>(type: "integer", nullable: false),
                    sftp_passphrase_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    sftp_passphrase_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    opds_base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    opds_username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    protected_opds_password = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    opds_password_format_version = table.Column<int>(type: "integer", nullable: false),
                    opds_password_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    opds_password_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cwa_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "publishing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_item_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_deliveries_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "acquisition",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "library_imports",
                schema: "publishing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_book_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_imports", x => x.id);
                    table.ForeignKey(
                        name: "FK_library_imports_media_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "acquisition",
                        principalTable: "media_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_asset_id",
                schema: "publishing",
                table: "deliveries",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "IX_library_imports_asset_id",
                schema: "publishing",
                table: "library_imports",
                column: "asset_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audiobookshelf_settings",
                schema: "publishing");

            migrationBuilder.DropTable(
                name: "cwa_settings",
                schema: "publishing");

            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "publishing");

            migrationBuilder.DropTable(
                name: "library_imports",
                schema: "publishing");
        }
    }
}
