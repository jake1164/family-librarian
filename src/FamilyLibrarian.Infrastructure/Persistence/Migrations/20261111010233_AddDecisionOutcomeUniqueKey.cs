using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionOutcomeUniqueKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_decision_outcomes_snapshot_id_kind_candidate_key",
                schema: "tuning",
                table: "decision_outcomes",
                columns: new[] { "snapshot_id", "kind", "candidate_key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_decision_outcomes_snapshot_id_kind_candidate_key",
                schema: "tuning",
                table: "decision_outcomes");
        }
    }
}
