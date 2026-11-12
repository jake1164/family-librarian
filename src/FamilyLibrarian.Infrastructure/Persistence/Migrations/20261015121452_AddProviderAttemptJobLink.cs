using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAttemptJobLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "provider_acquisition_job_id",
                schema: "acquisition",
                table: "provider_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_attempts_provider_acquisition_job_id",
                schema: "acquisition",
                table: "provider_attempts",
                column: "provider_acquisition_job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_attempts_provider_acquisition_job_id",
                schema: "acquisition",
                table: "provider_attempts");

            migrationBuilder.DropColumn(
                name: "provider_acquisition_job_id",
                schema: "acquisition",
                table: "provider_attempts");
        }
    }
}
