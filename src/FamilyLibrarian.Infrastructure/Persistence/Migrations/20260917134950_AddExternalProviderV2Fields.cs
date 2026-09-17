using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalProviderV2Fields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cached_acquire_operation_status",
                schema: "providers",
                table: "external_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cached_documentation_url",
                schema: "providers",
                table: "external_providers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cached_health_status",
                schema: "providers",
                table: "external_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cached_instance_id",
                schema: "providers",
                table: "external_providers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cached_management_url",
                schema: "providers",
                table: "external_providers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cached_search_operation_status",
                schema: "providers",
                table: "external_providers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "instance_replaced_since_previous_test",
                schema: "providers",
                table: "external_providers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cached_acquire_operation_status",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "cached_documentation_url",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "cached_health_status",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "cached_instance_id",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "cached_management_url",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "cached_search_operation_status",
                schema: "providers",
                table: "external_providers");

            migrationBuilder.DropColumn(
                name: "instance_replaced_since_previous_test",
                schema: "providers",
                table: "external_providers");
        }
    }
}
