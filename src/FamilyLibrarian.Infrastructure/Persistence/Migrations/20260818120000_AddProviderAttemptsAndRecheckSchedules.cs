using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(AppDbContext))]
[Migration("20260818120000_AddProviderAttemptsAndRecheckSchedules")]
public partial class AddProviderAttemptsAndRecheckSchedules : Migration
{
    private static readonly string[] RequestAttemptIndexColumns = ["request_id", "attempted_at_utc"];
    private static readonly string[] FormatProviderAttemptIndexColumns = ["request_format_id", "provider_id", "attempted_at_utc"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "recheck_schedule",
            schema: "providers",
            table: "external_providers",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "Manual");

        migrationBuilder.CreateTable(
            name: "provider_attempts",
            schema: "acquisition",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                request_id = table.Column<Guid>(type: "uuid", nullable: false),
                request_format_id = table.Column<Guid>(type: "uuid", nullable: false),
                provider_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                attempted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                next_eligible_check_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_attempts", x => x.id);
                table.ForeignKey(
                    name: "FK_provider_attempts_book_requests_request_id",
                    column: x => x.request_id,
                    principalSchema: "requests",
                    principalTable: "book_requests",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_provider_attempts_request_formats_request_format_id",
                    column: x => x.request_format_id,
                    principalSchema: "requests",
                    principalTable: "request_formats",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_provider_attempts_request_id_attempted_at_utc",
            schema: "acquisition",
            table: "provider_attempts",
            columns: RequestAttemptIndexColumns);

        migrationBuilder.CreateIndex(
            name: "IX_provider_attempts_request_format_id_provider_id_attempted_at_utc",
            schema: "acquisition",
            table: "provider_attempts",
            columns: FormatProviderAttemptIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "provider_attempts", schema: "acquisition");

        migrationBuilder.DropColumn(
            name: "recheck_schedule",
            schema: "providers",
            table: "external_providers");
    }

    /// <summary>
    /// The model snapshot is authoritative for later migration generation. The
    /// migration itself is intentionally explicit because it adds an audit
    /// ledger and a non-null backfill default.
    /// </summary>
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
    }
}
