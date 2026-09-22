using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAudiobookNarrationPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing account backfills to PreferHuman -- the same
            // default a brand new account gets -- so no account needs a
            // manual repair after upgrade (AppUser.AudiobookNarrationPreference's
            // remarks).
            migrationBuilder.AddColumn<string>(
                name: "audiobook_narration_preference",
                schema: "identity",
                table: "users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "PreferHuman");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audiobook_narration_preference",
                schema: "identity",
                table: "users");
        }
    }
}
