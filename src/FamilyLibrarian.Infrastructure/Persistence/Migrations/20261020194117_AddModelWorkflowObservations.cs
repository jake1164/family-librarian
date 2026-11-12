using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelWorkflowObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "model_usage_record_id",
                schema: "evidence",
                table: "semantic_findings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "workflow_effects",
                schema: "integrations",
                table: "model_usage_ledger",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model_usage_record_id",
                schema: "evidence",
                table: "semantic_findings");

            migrationBuilder.DropColumn(
                name: "workflow_effects",
                schema: "integrations",
                table: "model_usage_ledger");
        }
    }
}
