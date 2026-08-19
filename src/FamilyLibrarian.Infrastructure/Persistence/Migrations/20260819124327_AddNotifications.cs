using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates arrays for composite migration operations.

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "notification_events",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    audience = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    subject_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    subject_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    repeat_count = table.Column<int>(type: "integer", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_receipts",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    read_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_receipts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_audience_recipient_user_id_category_sub~",
                schema: "notifications",
                table: "notification_events",
                columns: new[] { "audience", "recipient_user_id", "category", "subject_type", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_events_last_occurred_at_utc",
                schema: "notifications",
                table: "notification_events",
                column: "last_occurred_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_notification_receipts_notification_event_id_user_id",
                schema: "notifications",
                table: "notification_receipts",
                columns: new[] { "notification_event_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_events",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notification_receipts",
                schema: "notifications");
        }
    }
}

#pragma warning restore CA1861
