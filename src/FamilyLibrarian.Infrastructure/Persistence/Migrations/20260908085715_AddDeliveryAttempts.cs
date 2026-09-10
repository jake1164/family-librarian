using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                schema: "delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    external_book_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    book_format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    convert = table.Column<bool>(type: "boolean", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_retryable = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_delivery_targets_delivery_target_id",
                        column: x => x.delivery_target_id,
                        principalSchema: "delivery",
                        principalTable: "delivery_targets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_delivery_target_id",
                schema: "delivery",
                table: "delivery_attempts",
                column: "delivery_target_id");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_request_id_user_id",
                schema: "delivery",
                table: "delivery_attempts",
                columns: new[] { "request_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_status_is_retryable_completed_at_utc",
                schema: "delivery",
                table: "delivery_attempts",
                columns: new[] { "status", "is_retryable", "completed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_user_id",
                schema: "delivery",
                table: "delivery_attempts",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_attempts",
                schema: "delivery");
        }
    }
}
