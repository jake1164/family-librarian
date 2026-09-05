using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShareHouseholdRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_book_requests_work_id",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.AddColumn<bool>(
                name: "requires_manual_fulfillment",
                schema: "requests",
                table: "book_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "version_details",
                schema: "requests",
                table: "book_requests",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "version_kind",
                schema: "requests",
                table: "book_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "request_participants",
                schema: "requests",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    wants_ebook = table.Column<bool>(type: "boolean", nullable: false),
                    wants_audiobook = table.Column<bool>(type: "boolean", nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    joined_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    withdrawn_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_participants", x => new { x.request_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_request_participants_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_request_participants_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Preserve ownership, notes, request/format IDs and attached fulfillment history.
            migrationBuilder.Sql("""
                INSERT INTO requests.request_participants
                    (request_id, user_id, wants_ebook, wants_audiobook, note, joined_at_utc, withdrawn_at_utc)
                SELECT r.id, r.user_id,
                    EXISTS (SELECT 1 FROM requests.request_formats f WHERE f.request_id = r.id AND f.media_type = 'Ebook'),
                    EXISTS (SELECT 1 FROM requests.request_formats f WHERE f.request_id = r.id AND f.media_type = 'Audiobook'),
                    r.requester_note, r.requested_at_utc,
                    CASE WHEN r.status = 'Cancelled' THEN r.status_changed_at_utc ELSE NULL END
                FROM requests.book_requests r;

                -- Historical repeats may represent distinct versions or have in-flight files.
                -- Keep their IDs and require review rather than guessing a destructive merge.
                WITH ranked AS (
                    SELECT id, status, row_number() OVER (PARTITION BY work_id ORDER BY requested_at_utc, id) AS ordinal,
                        count(*) OVER (PARTITION BY work_id) AS copies
                    FROM requests.book_requests
                    WHERE status IN ('PendingAcquisition', 'NeedsReview')
                )
                INSERT INTO requests.request_status_history
                    (id, request_id, from_status, to_status, actor_user_id, reason, occurred_at_utc)
                SELECT gen_random_uuid(), id, status, 'NeedsReview', NULL,
                    'Overlapping requests found during upgrade; review existing files before further acquisition.', now()
                FROM ranked WHERE copies > 1;

                WITH ranked AS (
                    SELECT id, row_number() OVER (PARTITION BY work_id ORDER BY requested_at_utc, id) AS ordinal,
                        count(*) OVER (PARTITION BY work_id) AS copies
                    FROM requests.book_requests
                    WHERE status IN ('PendingAcquisition', 'NeedsReview')
                )
                UPDATE requests.book_requests r
                SET status = 'NeedsReview', status_changed_at_utc = now(), updated_at_utc = now(),
                    requires_manual_fulfillment = ranked.ordinal > 1,
                    version_kind = CASE WHEN ranked.ordinal > 1 THEN 'LegacyOverlap' ELSE NULL END,
                    version_details = CASE WHEN ranked.ordinal > 1 THEN
                        'Existing overlapping request retained for review. Inspect its files and history before choosing a copy.' ELSE NULL END
                FROM ranked WHERE r.id = ranked.id AND ranked.copies > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_book_requests_work_id",
                schema: "requests",
                table: "book_requests",
                column: "work_id",
                unique: true,
                filter: "status IN ('PendingAcquisition', 'NeedsReview') AND NOT requires_manual_fulfillment");

            migrationBuilder.CreateIndex(
                name: "IX_request_participants_user_id",
                schema: "requests",
                table: "request_participants",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_participants",
                schema: "requests");

            migrationBuilder.DropIndex(
                name: "IX_book_requests_work_id",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.DropColumn(
                name: "requires_manual_fulfillment",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.DropColumn(
                name: "version_details",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.DropColumn(
                name: "version_kind",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.CreateIndex(
                name: "IX_book_requests_work_id",
                schema: "requests",
                table: "book_requests",
                column: "work_id");
        }
    }
}
