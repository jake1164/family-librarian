using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAcquisitionJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_acquisition_jobs",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_instance_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    provider_job_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    candidate_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    candidate_revision = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    acquire_token = table.Column<string>(type: "text", nullable: true),
                    lifecycle_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    interaction_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    interaction_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    interaction_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    interaction_resume_supported = table.Column<bool>(type: "boolean", nullable: true),
                    interaction_action_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    progress_percent = table.Column<double>(type: "double precision", nullable: true),
                    progress_bytes_completed = table.Column<long>(type: "bigint", nullable: true),
                    progress_bytes_total = table.Column<long>(type: "bigint", nullable: true),
                    progress_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    error_retryable = table.Column<bool>(type: "boolean", nullable: true),
                    error_retry_after_seconds = table.Column<int>(type: "integer", nullable: true),
                    error_details_json = table.Column<string>(type: "jsonb", nullable: true),
                    retention_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_poll_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    extensions_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_acquisition_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_acquisition_jobs_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_acquisition_jobs_external_providers_external_provi~",
                        column: x => x.external_provider_id,
                        principalSchema: "providers",
                        principalTable: "external_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_provider_acquisition_jobs_request_formats_request_format_id",
                        column: x => x.request_format_id,
                        principalSchema: "requests",
                        principalTable: "request_formats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_acquisition_job_outputs",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_acquisition_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    output_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    filename = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    content_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    uri = table.Column<string>(type: "text", nullable: true),
                    uri_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    checksums_json = table.Column<string>(type: "jsonb", nullable: true),
                    retention_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_acquisition_job_outputs", x => x.id);
                    table.ForeignKey(
                        name: "FK_provider_acquisition_job_outputs_provider_acquisition_jobs_~",
                        column: x => x.provider_acquisition_job_id,
                        principalSchema: "acquisition",
                        principalTable: "provider_acquisition_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_job_outputs_provider_acquisition_job_id",
                schema: "acquisition",
                table: "provider_acquisition_job_outputs",
                column: "provider_acquisition_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_jobs_external_provider_id_idempotency_~",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                columns: new[] { "external_provider_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_jobs_next_poll_at_utc",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                column: "next_poll_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_jobs_request_format_id_lifecycle_state",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                columns: new[] { "request_format_id", "lifecycle_state" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_acquisition_jobs_request_id",
                schema: "acquisition",
                table: "provider_acquisition_jobs",
                column: "request_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_acquisition_job_outputs",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "provider_acquisition_jobs",
                schema: "acquisition");
        }
    }
}
