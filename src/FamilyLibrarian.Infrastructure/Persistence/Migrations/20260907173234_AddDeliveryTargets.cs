using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "delivery");

            migrationBuilder.AddColumn<Guid>(
                name: "delivery_target_id",
                schema: "requests",
                table: "request_participants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "delivery_targets",
                schema: "delivery",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_targets", x => x.id);
                    table.ForeignKey(
                        name: "FK_delivery_targets_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_request_participants_delivery_target_id",
                schema: "requests",
                table: "request_participants",
                column: "delivery_target_id");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_targets_user_id",
                schema: "delivery",
                table: "delivery_targets",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_request_participants_delivery_targets_delivery_target_id",
                schema: "requests",
                table: "request_participants",
                column: "delivery_target_id",
                principalSchema: "delivery",
                principalTable: "delivery_targets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_request_participants_delivery_targets_delivery_target_id",
                schema: "requests",
                table: "request_participants");

            migrationBuilder.DropTable(
                name: "delivery_targets",
                schema: "delivery");

            migrationBuilder.DropIndex(
                name: "IX_request_participants_delivery_target_id",
                schema: "requests",
                table: "request_participants");

            migrationBuilder.DropColumn(
                name: "delivery_target_id",
                schema: "requests",
                table: "request_participants");
        }
    }
}
