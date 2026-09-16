using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatrixCommunications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "matrix_settings",
                schema: "communications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    homeserver_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    bot_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    protected_access_token = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    access_token_format_version = table.Column<int>(type: "integer", nullable: false),
                    access_token_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_sync_token = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matrix_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_matrix_destinations",
                schema: "communications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    matrix_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    room_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    verification_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    verification_requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_matrix_destinations", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_matrix_destinations_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_matrix_destinations_room_id",
                schema: "communications",
                table: "user_matrix_destinations",
                column: "room_id",
                unique: true,
                filter: "room_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_user_matrix_destinations_user_id",
                schema: "communications",
                table: "user_matrix_destinations",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "matrix_settings",
                schema: "communications");

            migrationBuilder.DropTable(
                name: "user_matrix_destinations",
                schema: "communications");
        }
    }
}
