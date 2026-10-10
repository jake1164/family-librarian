using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds the administrator-selected provider-native acquisition path. Existing
/// providers backfill to FreeOnly so upgrading cannot begin consuming a
/// provider's subscription quota without an explicit administrator choice.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260924003000_AddExternalProviderAcquisitionMode")]
public partial class AddExternalProviderAcquisitionMode : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "acquisition_mode",
            schema: "providers",
            table: "external_providers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "FreeOnly");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "acquisition_mode",
            schema: "providers",
            table: "external_providers");
    }
}
