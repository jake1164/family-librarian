using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryAttemptConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "book_title",
                schema: "delivery",
                table: "delivery_attempts",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "confirmation_status",
                schema: "delivery",
                table: "delivery_attempts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unconfirmed");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "confirmed_at_utc",
                schema: "delivery",
                table: "delivery_attempts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "book_title",
                schema: "delivery",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "confirmation_status",
                schema: "delivery",
                table: "delivery_attempts");

            migrationBuilder.DropColumn(
                name: "confirmed_at_utc",
                schema: "delivery",
                table: "delivery_attempts");
        }
    }
}
