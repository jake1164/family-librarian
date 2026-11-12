using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetOriginAndHoldCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "identity_hold_code",
                schema: "acquisition",
                table: "media_assets",
                type: "character varying(96)",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                schema: "acquisition",
                table: "media_assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<Guid>(
                name: "origin_user_id",
                schema: "acquisition",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            // Origin is a statement of fact made once, by the code path that created the asset. Protect it at the
            // database as well as in the domain model: any UPDATE that would change it is rejected. Existing rows
            // carry the explicit value 'Legacy' from the column default above and are never reclassified.
            migrationBuilder.Sql("""
                CREATE FUNCTION acquisition.media_assets_protect_origin() RETURNS trigger AS $$
                BEGIN
                    IF NEW.origin IS DISTINCT FROM OLD.origin OR NEW.origin_user_id IS DISTINCT FROM OLD.origin_user_id THEN
                        RAISE EXCEPTION 'media_assets.origin and origin_user_id are immutable once the asset exists';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER media_assets_protect_origin
                BEFORE UPDATE ON acquisition.media_assets
                FOR EACH ROW EXECUTE FUNCTION acquisition.media_assets_protect_origin();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS media_assets_protect_origin ON acquisition.media_assets;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS acquisition.media_assets_protect_origin();");

            migrationBuilder.DropColumn(
                name: "identity_hold_code",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "acquisition",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "origin_user_id",
                schema: "acquisition",
                table: "media_assets");
        }
    }
}
