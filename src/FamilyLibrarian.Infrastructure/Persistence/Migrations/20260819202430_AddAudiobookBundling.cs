using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudiobookBundling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "bundle_id",
                schema: "acquisition",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "bundle_sequence",
                schema: "acquisition",
                table: "media_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "bundle_track_count",
                schema: "acquisition",
                table: "media_assets",
                type: "integer",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "asset_id",
                schema: "publishing",
                table: "deliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "bundle_id",
                schema: "publishing",
                table: "deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_bundle_id",
                schema: "acquisition",
                table: "media_assets",
                column: "bundle_id");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_bundle_id",
                schema: "publishing",
                table: "deliveries",
                column: "bundle_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_media_assets_bundle_id",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "IX_deliveries_bundle_id",
                schema: "publishing",
                table: "deliveries");

            migrationBuilder.DropColumn(
                name: "bundle_id",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "bundle_sequence",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "bundle_track_count",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "bundle_id",
                schema: "publishing",
                table: "deliveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "asset_id",
                schema: "publishing",
                table: "deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
