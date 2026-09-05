using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>
/// One throwaway PostgreSQL container shared by the whole test assembly, handing
/// out a freshly migrated database per test class.
/// </summary>
/// <remarks>
/// A real PostgreSQL instance rather than the in-memory EF provider, because these
/// tests exercise Identity, the Data Protection key ring, and schema-qualified
/// tables — none of which the in-memory provider models faithfully.
/// <para>
/// Starting the container once and creating a database per class keeps the ~5s
/// image cost off every class while still isolating provider-settings mutations,
/// which would otherwise leak between classes.
/// </para>
/// </remarks>
internal static class PostgresFixture
{
    private static PostgreSqlContainer? _container;

    /// <summary>
    /// Why the container could not start, or <c>null</c> when it is running.
    /// Tests report this as inconclusive rather than failing, so a machine without
    /// Docker does not look like a broken build.
    /// </summary>
    internal static string? UnavailableReason { get; private set; } =
        "The PostgreSQL test container has not been started.";

    internal static async Task StartAsync()
    {
        PostgreSqlContainer? container = null;
        try
        {
            // Build validates Docker availability too, not only StartAsync. Keep
            // it inside this guard so a host without Docker reaches the suite's
            // documented inconclusive path instead of failing assembly setup.
            container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("family_librarian_tests")
                .WithUsername("family_librarian")
                .WithPassword("family_librarian")
                .Build();
            await container.StartAsync();
        }
        catch (Exception exception)
        {
            // Docker missing, not running, or unable to pull the image. Record the
            // reason and let each test skip; do not fail the assembly.
            UnavailableReason =
                $"A PostgreSQL test container could not be started, so host-integration tests were skipped: {exception.Message}";
            if (container is not null)
            {
                await container.DisposeAsync();
            }
            return;
        }

        _container = container!;
        UnavailableReason = null;
    }

    internal static async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }

        UnavailableReason = "The PostgreSQL test container has been stopped.";
    }

    /// <summary>
    /// Creates a database and migrates it to <paramref name="targetMigration"/>
    /// or, when omitted, the current migration. Tests use an explicit historical
    /// target to prove that a deployed installation can upgrade forward rather
    /// than only proving a brand-new database works.
    /// </summary>
    internal static async Task<string> CreateMigratedDatabaseAsync(
        string? targetMigration = null)
    {
        if (_container is null)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        var databaseName = $"fl_{Guid.NewGuid():N}";

        var adminConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Pooling = false
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            // The name is a generated GUID, never caller input, and PostgreSQL does
            // not accept a parameter in this position.
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        // Every database has a different connection string and therefore its own
        // pool. Context disposal returns connections to those pools, retaining
        // server slots across classes until idle pruning. These short-lived test
        // databases need physical connections closed as soon as they are disposed.
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName
        }.ConnectionString;

        // Migrate before the host boots: Program.cs seeds roles and the bootstrap
        // administrator during startup and would fault against an empty database.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync(targetMigration);

        return connectionString;
    }
}
