using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeProviderModel : Migration
    {
        // Hand-edited after scaffolding: the default scaffold treated the
        // MetadataProviderSetting -> ProviderSetting rename as a drop+create,
        // which would destroy every stored, encrypted provider credential. A
        // schema move plus table/constraint rename preserves the existing rows
        // (and their ciphertext) exactly as they are — see the M8 rename plan
        // and the "DO NOT CHANGE" note on DataProtectionCredentialProtector's
        // PurposeRoot, which is what makes that ciphertext still decryptable
        // after this migration runs.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "providers");

            migrationBuilder.RenameTable(
                name: "metadata_provider_settings",
                schema: "app",
                newName: "provider_settings",
                newSchema: "providers");

            migrationBuilder.Sql(
                "ALTER TABLE providers.provider_settings " +
                "RENAME CONSTRAINT \"PK_metadata_provider_settings\" TO \"PK_provider_settings\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE providers.provider_settings " +
                "RENAME CONSTRAINT \"PK_provider_settings\" TO \"PK_metadata_provider_settings\";");

            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.RenameTable(
                name: "provider_settings",
                schema: "providers",
                newName: "metadata_provider_settings",
                newSchema: "app");
        }
    }
}
