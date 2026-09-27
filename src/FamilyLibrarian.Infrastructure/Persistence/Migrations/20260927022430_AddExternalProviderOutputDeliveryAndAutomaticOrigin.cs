using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalProviderOutputDeliveryAndAutomaticOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_acquisition_job_outputs_provider_acquisition_job_id",
                schema: "acquisition",
                table: "provider_acquisition_job_outputs");

            migrationBuilder.AddColumn<Guid>(
                name: "acquire_request_id", schema: "acquisition", table: "provider_acquisition_jobs",
                type: "uuid", nullable: true);
            migrationBuilder.Sql("UPDATE acquisition.provider_acquisition_jobs SET acquire_request_id = gen_random_uuid();");
            migrationBuilder.AlterColumn<Guid>(
                name: "acquire_request_id", schema: "acquisition", table: "provider_acquisition_jobs",
                type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "acquisition_mode", schema: "acquisition", table: "provider_acquisition_jobs",
                type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "FreeOnly");
            migrationBuilder.AddColumn<Guid>(
                name: "local_acquisition_job_id", schema: "acquisition", table: "provider_acquisition_jobs",
                type: "uuid", nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "is_automatic_acquisition", schema: "acquisition", table: "provider_acquisition_jobs",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.Sql(
                "UPDATE acquisition.provider_acquisition_jobs AS job SET is_automatic_acquisition = TRUE " +
                "WHERE EXISTS (SELECT 1 FROM acquisition.provider_attempts AS attempt " +
                "WHERE attempt.request_id = job.request_id AND attempt.request_format_id = job.request_format_id " +
                "AND LOWER(attempt.provider_id) = LOWER(job.provider_id) AND attempt.outcome = 'Submitted');");
            migrationBuilder.AddColumn<Guid>(
                name: "media_asset_id", schema: "acquisition", table: "provider_acquisition_job_outputs",
                type: "uuid", nullable: true);
            migrationBuilder.AddColumn<int>(
                name: "sequence", schema: "acquisition", table: "provider_acquisition_job_outputs",
                type: "integer", nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_jobs_local_acquisition_job_id",
                schema: "acquisition", table: "provider_acquisition_jobs", column: "local_acquisition_job_id");
            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_job_outputs_media_asset_id",
                schema: "acquisition", table: "provider_acquisition_job_outputs", column: "media_asset_id",
                unique: true, filter: "media_asset_id IS NOT NULL");
            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_job_outputs_provider_acquisition_job_i~",
                schema: "acquisition", table: "provider_acquisition_job_outputs",
                columns: new[] { "provider_acquisition_job_id", "output_id" }, unique: true);
            migrationBuilder.AddForeignKey(
                name: "FK_provider_acquisition_job_outputs_media_assets_media_asset_id",
                schema: "acquisition", table: "provider_acquisition_job_outputs", column: "media_asset_id",
                principalSchema: "acquisition", principalTable: "media_assets", principalColumn: "id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(
                name: "FK_provider_acquisition_jobs_acquisition_jobs_local_acquisitio~",
                schema: "acquisition", table: "provider_acquisition_jobs", column: "local_acquisition_job_id",
                principalSchema: "acquisition", principalTable: "acquisition_jobs", principalColumn: "id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "acquire_request_id", schema: "acquisition", table: "provider_acquisition_jobs");
            migrationBuilder.DropForeignKey(
                name: "FK_provider_acquisition_job_outputs_media_assets_media_asset_id",
                schema: "acquisition", table: "provider_acquisition_job_outputs");
            migrationBuilder.DropForeignKey(
                name: "FK_provider_acquisition_jobs_acquisition_jobs_local_acquisitio~",
                schema: "acquisition", table: "provider_acquisition_jobs");
            migrationBuilder.DropIndex(
                name: "IX_provider_acquisition_jobs_local_acquisition_job_id",
                schema: "acquisition", table: "provider_acquisition_jobs");
            migrationBuilder.DropIndex(
                name: "IX_provider_acquisition_job_outputs_media_asset_id",
                schema: "acquisition", table: "provider_acquisition_job_outputs");
            migrationBuilder.DropIndex(
                name: "IX_provider_acquisition_job_outputs_provider_acquisition_job_i~",
                schema: "acquisition", table: "provider_acquisition_job_outputs");
            migrationBuilder.DropColumn(
                name: "acquisition_mode", schema: "acquisition", table: "provider_acquisition_jobs");
            migrationBuilder.DropColumn(
                name: "local_acquisition_job_id", schema: "acquisition", table: "provider_acquisition_jobs");
            migrationBuilder.DropColumn(
                name: "is_automatic_acquisition", schema: "acquisition", table: "provider_acquisition_jobs");
            migrationBuilder.DropColumn(
                name: "media_asset_id", schema: "acquisition", table: "provider_acquisition_job_outputs");
            migrationBuilder.DropColumn(
                name: "sequence", schema: "acquisition", table: "provider_acquisition_job_outputs");
            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_job_outputs_provider_acquisition_job_id",
                schema: "acquisition", table: "provider_acquisition_job_outputs", column: "provider_acquisition_job_id");
        }
    }
}
