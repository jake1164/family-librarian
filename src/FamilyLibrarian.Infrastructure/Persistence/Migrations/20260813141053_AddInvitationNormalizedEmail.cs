using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationNormalizedEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invitations_email",
                schema: "identity",
                table: "invitations");

            migrationBuilder.AddColumn<string>(
                name: "normalized_email",
                schema: "identity",
                table: "invitations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            // Backfill rather than leave the default in place: an existing row
            // with an empty normalized address would never match an outstanding
            // invitation, so reissuing would silently leave two live tokens for
            // the same person.
            migrationBuilder.Sql(
                """
                UPDATE identity.invitations
                SET normalized_email = upper(email)
                WHERE normalized_email = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_invitations_normalized_email",
                schema: "identity",
                table: "invitations",
                column: "normalized_email");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_invitations_normalized_email",
                schema: "identity",
                table: "invitations");

            migrationBuilder.DropColumn(
                name: "normalized_email",
                schema: "identity",
                table: "invitations");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_email",
                schema: "identity",
                table: "invitations",
                column: "email");
        }
    }
}
