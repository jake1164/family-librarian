using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates arrays for composite migration operations.

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBookRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "requests");

            migrationBuilder.CreateTable(
                name: "book_requests",
                schema: "requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requester_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    admin_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_book_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_book_requests_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_book_requests_works_work_id",
                        column: x => x.work_id,
                        principalSchema: "catalog",
                        principalTable: "works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "request_formats",
                schema: "requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    media_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_formats", x => x.id);
                    table.ForeignKey(
                        name: "FK_request_formats_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "request_status_history",
                schema: "requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_status_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_request_status_history_book_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "requests",
                        principalTable: "book_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_book_requests_status_updated_at_utc",
                schema: "requests",
                table: "book_requests",
                columns: new[] { "status", "updated_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_book_requests_user_id_work_id_status",
                schema: "requests",
                table: "book_requests",
                columns: new[] { "user_id", "work_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_book_requests_work_id",
                schema: "requests",
                table: "book_requests",
                column: "work_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_formats_request_id_media_type",
                schema: "requests",
                table: "request_formats",
                columns: new[] { "request_id", "media_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_request_status_history_request_id_occurred_at_utc",
                schema: "requests",
                table: "request_status_history",
                columns: new[] { "request_id", "occurred_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_formats",
                schema: "requests");

            migrationBuilder.DropTable(
                name: "request_status_history",
                schema: "requests");

            migrationBuilder.DropTable(
                name: "book_requests",
                schema: "requests");
        }
    }
}

#pragma warning restore CA1861
