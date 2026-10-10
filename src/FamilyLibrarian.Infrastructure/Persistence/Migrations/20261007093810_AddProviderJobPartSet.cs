using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderJobPartSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "part_number",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "part_set_id",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "part_total",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_jobs_part_set_id",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                column: "part_set_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_acquisition_jobs_part_set_id",
                schema: "acquisition",
                table: "provider_acquisition_jobs");

            migrationBuilder.DropColumn(
                name: "part_number",
                schema: "acquisition",
                table: "provider_acquisition_jobs");

            migrationBuilder.DropColumn(
                name: "part_set_id",
                schema: "acquisition",
                table: "provider_acquisition_jobs");

            migrationBuilder.DropColumn(
                name: "part_total",
                schema: "acquisition",
                table: "provider_acquisition_jobs");
        }
    }
}
