using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityPreConfirmedTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "identity_pre_confirmed",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "identity_pre_confirmed",
                schema: "acquisition",
                table: "media_assets",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "identity_pre_confirmed",
                schema: "acquisition",
                table: "provider_acquisition_jobs");

            migrationBuilder.DropColumn(
                name: "identity_pre_confirmed",
                schema: "acquisition",
                table: "media_assets");
        }
    }
}
