using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "conversation");

            migrationBuilder.CreateTable(
                name: "processed_events",
                schema: "conversation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    reply_json = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_processed_events_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "request_keys",
                schema: "conversation",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_keys", x => x.key);
                    table.ForeignKey(
                        name: "FK_request_keys_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "conversation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transport = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    state_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    turn_count = table.Column<int>(type: "integer", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_processed_events_created_at_utc",
                schema: "conversation",
                table: "processed_events",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "IX_processed_events_transport_scope_key_event_id",
                schema: "conversation",
                table: "processed_events",
                columns: new[] { "transport", "scope_key", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processed_events_user_id",
                schema: "conversation",
                table: "processed_events",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_keys_user_id",
                schema: "conversation",
                table: "request_keys",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id_transport_scope_key",
                schema: "conversation",
                table: "sessions",
                columns: new[] { "user_id", "transport", "scope_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_events",
                schema: "conversation");

            migrationBuilder.DropTable(
                name: "request_keys",
                schema: "conversation");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "conversation");
        }
    }
}
