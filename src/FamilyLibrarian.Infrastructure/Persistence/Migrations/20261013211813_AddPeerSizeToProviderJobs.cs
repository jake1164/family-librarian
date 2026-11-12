using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerSizeToProviderJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "peer_count",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "peer_median_size_bytes",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "peer_count",
                schema: "acquisition",
                table: "provider_acquisition_jobs");

            migrationBuilder.DropColumn(
                name: "peer_median_size_bytes",
                schema: "acquisition",
                table: "provider_acquisition_jobs");
        }
    }
}
