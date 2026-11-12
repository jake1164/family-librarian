using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAssetOriginDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "origin",
                schema: "acquisition",
                table: "media_assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "Legacy");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_origin_defined",
                schema: "acquisition",
                table: "media_assets",
                sql: "origin IN ('Legacy', 'Unknown', 'ManualUpload', 'BuiltInProvider', 'ExternalProvider')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_origin_defined",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.AlterColumn<string>(
                name: "origin",
                schema: "acquisition",
                table: "media_assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Legacy",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
