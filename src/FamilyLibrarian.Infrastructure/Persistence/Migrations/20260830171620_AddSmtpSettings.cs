using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSmtpSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "communications");

            migrationBuilder.CreateTable(
                name: "smtp_settings",
                schema: "communications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    port = table.Column<int>(type: "integer", nullable: true),
                    security_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "StartTls"),
                    username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    protected_password = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    password_format_version = table.Column<int>(type: "integer", nullable: false),
                    password_set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    from_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    last_tested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_test_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_test_message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_smtp_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "smtp_settings",
                schema: "communications");
        }
    }
}
