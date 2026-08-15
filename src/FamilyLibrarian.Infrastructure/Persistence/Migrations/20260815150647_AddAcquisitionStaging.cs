using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAcquisitionStaging : Migration
    {
        // Hoisted out of the CreateIndex call to satisfy CA1861, which the build
        // treats as an error.
        private static readonly string[] AcquisitionJobRequestStatusIndexColumns = ["request_id", "status"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "acquisition");

            migrationBuilder.CreateTable(
                name: "acquisition_jobs",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    egress_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acquisition_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_acquisition_jobs_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "acquisition_candidates",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    acquisition_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    author = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    bitrate_kbps = table.Column<int>(type: "integer", nullable: true),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    confidence_score = table.Column<double>(type: "double precision", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acquisition_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_acquisition_candidates_acquisition_jobs_acquisition_job_id",
                        column: x => x.acquisition_job_id,
                        principalSchema: "acquisition",
                        principalTable: "acquisition_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                schema: "acquisition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    edition_id = table.Column<Guid>(type: "uuid", nullable: true),
                    media_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    original_filename = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    stored_filename = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    detected_mime_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    associated_request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_acquisition_candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_assets", x => x.id);
                    table.ForeignKey(
                        name: "FK_media_assets_acquisition_candidates_source_acquisition_cand~",
                        column: x => x.source_acquisition_candidate_id,
                        principalSchema: "acquisition",
                        principalTable: "acquisition_candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_assets_request_formats_associated_request_format_id",
                        column: x => x.associated_request_format_id,
                        principalSchema: "requests",
                        principalTable: "request_formats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_media_assets_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_acquisition_candidates_acquisition_job_id",
                schema: "acquisition",
                table: "acquisition_candidates",
                column: "acquisition_job_id");

            migrationBuilder.CreateIndex(
                name: "IX_acquisition_jobs_request_id_status",
                schema: "acquisition",
                table: "acquisition_jobs",
                columns: AcquisitionJobRequestStatusIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_associated_request_format_id",
                schema: "acquisition",
                table: "media_assets",
                column: "associated_request_format_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_sha256",
                schema: "acquisition",
                table: "media_assets",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_source_acquisition_candidate_id",
                schema: "acquisition",
                table: "media_assets",
                column: "source_acquisition_candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_work_id",
                schema: "acquisition",
                table: "media_assets",
                column: "work_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "acquisition_candidates",
                schema: "acquisition");

            migrationBuilder.DropTable(
                name: "acquisition_jobs",
                schema: "acquisition");
        }
    }
}
