using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAcquisitionQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accepted_variant_detail",
                schema: "requests",
                table: "request_formats",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "accepted_variant_kind",
                schema: "requests",
                table: "request_formats",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "matrix_event_id",
                schema: "communications",
                table: "outbound_communications",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "question_asked_at_utc",
                schema: "requests",
                table: "book_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "question_format_id",
                schema: "requests",
                table: "book_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "question_kind",
                schema: "requests",
                table: "book_requests",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "question_reminded_at_utc",
                schema: "requests",
                table: "book_requests",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accepted_variant_detail",
                schema: "requests",
                table: "request_formats");

            migrationBuilder.DropColumn(
                name: "accepted_variant_kind",
                schema: "requests",
                table: "request_formats");

            migrationBuilder.DropColumn(
                name: "matrix_event_id",
                schema: "communications",
                table: "outbound_communications");

            migrationBuilder.DropColumn(
                name: "question_asked_at_utc",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.DropColumn(
                name: "question_format_id",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.DropColumn(
                name: "question_kind",
                schema: "requests",
                table: "book_requests");

            migrationBuilder.DropColumn(
                name: "question_reminded_at_utc",
                schema: "requests",
                table: "book_requests");
        }
    }
}
