using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WidenDecisionCallHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "rendered_messages_hash",
                schema: "tuning",
                table: "decision_calls",
                type: "character varying(96)",
                maxLength: 96,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(80)",
                oldMaxLength: 80);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "rendered_messages_hash",
                schema: "tuning",
                table: "decision_calls",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(96)",
                oldMaxLength: 96);
        }
    }
}
