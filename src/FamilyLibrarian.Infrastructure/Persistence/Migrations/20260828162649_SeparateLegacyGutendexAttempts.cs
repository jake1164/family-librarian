using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FamilyLibrarian.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeparateLegacyGutendexAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The local Project Gutenberg implementation retains "gutendex" as
            // its provider key so current settings remain valid. These rows
            // predate that implementation and record calls to the retired HTTP
            // API, so give their history a distinct, non-active provider key.
            migrationBuilder.Sql(
                "UPDATE acquisition.provider_attempts " +
                "SET provider_id = 'gutendex-http-legacy' " +
                "WHERE provider_id = 'gutendex';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE acquisition.provider_attempts " +
                "SET provider_id = 'gutendex' " +
                "WHERE provider_id = 'gutendex-http-legacy';");
        }
    }
}
