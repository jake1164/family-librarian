using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelReasoningMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "reasoning_mode",
                schema: "integrations",
                table: "model_test_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inherit");

            migrationBuilder.AddColumn<string>(
                name: "reasoning_mode",
                schema: "integrations",
                table: "model_role_settings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inherit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reasoning_mode",
                schema: "integrations",
                table: "model_test_settings");

            migrationBuilder.DropColumn(
                name: "reasoning_mode",
                schema: "integrations",
                table: "model_role_settings");
        }
    }
}
