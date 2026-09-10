using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenKindleDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "delivery_id",
                schema: "delivery",
                table: "delivery_attempts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Legacy retries had no chain key and could reuse ordinal numbers.
            // Group request deliveries by participant; conservatively group old
            // standalone sends by recipient/book. Retain every row and its ID.
            migrationBuilder.Sql("""
                WITH history AS (
                    SELECT id,
                        first_value(id) OVER chain AS delivery_id,
                        row_number() OVER chain AS ordinal
                    FROM delivery.delivery_attempts
                    WINDOW chain AS (
                        PARTITION BY request_id, user_id,
                            CASE WHEN request_id IS NULL THEN delivery_target_id END,
                            CASE WHEN request_id IS NULL THEN provider END,
                            CASE WHEN request_id IS NULL THEN external_book_id END
                        ORDER BY created_at_utc, id
                    )
                )
                UPDATE delivery.delivery_attempts AS attempt
                SET delivery_id = history.delivery_id, attempt_number = history.ordinal
                FROM history WHERE attempt.id = history.id;

                -- Old transport failures did not distinguish a lost response
                -- from a send that never started. Do not replay them on upgrade.
                UPDATE delivery.delivery_attempts
                SET status = 'SubmissionUnknown', is_retryable = false,
                    failure_reason = 'This earlier send may already have been accepted. Check your Kindle before resending; another send may create a duplicate.'
                WHERE status = 'Failed' AND is_retryable;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_delivery_chain_attempt",
                schema: "delivery",
                table: "delivery_attempts",
                columns: new[] { "delivery_id", "attempt_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_delivery_request_user",
                schema: "delivery",
                table: "delivery_attempts",
                columns: new[] { "request_id", "user_id" },
                unique: true,
                filter: "request_id IS NOT NULL AND attempt_number = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE delivery.delivery_attempts SET status = 'Failed', is_retryable = false
                WHERE status = 'SubmissionUnknown';
                """);

            migrationBuilder.DropIndex(
                name: "ux_delivery_chain_attempt",
                schema: "delivery",
                table: "delivery_attempts");

            migrationBuilder.DropIndex(
                name: "ux_delivery_request_user",
                schema: "delivery",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "delivery_id",
                schema: "delivery",
                table: "delivery_attempts");
        }
    }
}
