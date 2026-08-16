using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "external_providers",
                schema: "providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    base_url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    protected_api_key = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    api_key_format_version = table.Column<int>(type: "integer", nullable: false),
                    api_key_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    api_key_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cached_protocol_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    cached_capabilities = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    cached_egress_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
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
                    table.PrimaryKey("PK_external_providers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "private_egress_gateway_settings",
                schema: "providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    gateway_endpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
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
                    table.PrimaryKey("PK_private_egress_gateway_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_catalogs",
                schema: "providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    cached_entries_json = table.Column<string>(type: "jsonb", nullable: true),
                    last_fetched_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_fetch_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_fetch_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_catalogs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_providers_provider_id",
                schema: "providers",
                table: "external_providers",
                column: "provider_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_providers",
                schema: "providers");

            migrationBuilder.DropTable(
                name: "private_egress_gateway_settings",
                schema: "providers");

            migrationBuilder.DropTable(
                name: "provider_catalogs",
                schema: "providers");
        }
    }
}
