using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveFamilyLibrarianEgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE acquisition.acquisition_jobs " +
                "SET status = 'Failed', " +
                "failure_reason = COALESCE(failure_reason, 'FL private egress routing was removed.') " +
                "WHERE status = 'WaitingForPrivateEgress'");

            migrationBuilder.DropTable(
                name: "private_egress_gateway_settings",
                schema: "providers");

            migrationBuilder.DropColumn(
                name: "cached_egress_policy",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "overridden_egress_policy",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "egress_policy",
                schema: "acquisition",
                table: "acquisition_jobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cached_egress_policy",
                schema: "providers",
                table: "external_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddColumn<string>(
                name: "overridden_egress_policy",
                schema: "providers",
                table: "external_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "egress_policy",
                schema: "acquisition",
                table: "acquisition_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.CreateTable(
                name: "private_egress_gateway_settings",
                schema: "providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    gateway_endpoint = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_test_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_private_egress_gateway_settings", x => x.id);
                });
        }
    }
}
