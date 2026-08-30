using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF Core generates arrays for composite migration operations.

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundCommunications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outbound_communications",
                schema: "communications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    communication_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    related_entity_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    related_entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    link = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_communications", x => x.id);
                    table.ForeignKey(
                        name: "FK_outbound_communications_users_recipient_user_id",
                        column: x => x.recipient_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbound_communication_deliveries",
                schema: "communications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    outbound_communication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    attempted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_communication_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_outbound_communication_deliveries_outbound_communications_o~",
                        column: x => x.outbound_communication_id,
                        principalSchema: "communications",
                        principalTable: "outbound_communications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_communication_deliveries_outbound_communication_id~",
                schema: "communications",
                table: "outbound_communication_deliveries",
                columns: new[] { "outbound_communication_id", "provider_id" });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_communications_processed_at_utc_created_at_utc",
                schema: "communications",
                table: "outbound_communications",
                columns: new[] { "processed_at_utc", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_outbound_communications_recipient_user_id",
                schema: "communications",
                table: "outbound_communications",
                column: "recipient_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbound_communication_deliveries",
                schema: "communications");

            migrationBuilder.DropTable(
                name: "outbound_communications",
                schema: "communications");
        }
    }
}

#pragma warning restore CA1861
