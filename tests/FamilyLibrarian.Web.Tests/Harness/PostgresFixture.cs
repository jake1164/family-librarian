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
/// <para>
/// Replaying every migration for each of those databases used to dominate the
/// suite, so the current schema is migrated once into a template and each new
/// database is a <c>CREATE DATABASE … TEMPLATE</c> copy of it. A second
/// template additionally carries the seeding every host-integration fixture
/// needs (roles, bootstrap administrator, the ordinary reader). A historical
/// migration target is never cloned: upgrade tests still migrate a database
/// from scratch to exactly that point.
/// </para>
/// </remarks>
internal static class PostgresFixture
{
    private const string MigratedTemplateName = "fl_template_migrated";
    private const string SeededTemplateName = "fl_template_seeded";

    // PostgreSQL refuses to copy a template another session is connected to and
    // does not document concurrent copies of one template, so clones queue here.
    // A copy of these small templates takes milliseconds.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    private static PostgreSqlContainer? _container;
    private static bool _seededTemplateReady;

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
                // Test classes run in parallel and every connection is unpooled,
                // so leave headroom over PostgreSQL's default of 100.
                .WithCommand("-c", "max_connections=400")
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

        await CreateDatabaseAsync(MigratedTemplateName);
        await MigrateAsync(ConnectionStringFor(MigratedTemplateName), targetMigration: null);
        await SealTemplateAsync(MigratedTemplateName);
    }

    /// <summary>
    /// Builds the seeded template from the migrated one. The caller supplies the
    /// seeding so this class stays independent of the host it boots.
    /// </summary>
    internal static async Task PrepareSeededTemplateAsync(Func<string, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        RequireContainer();

        await CloneAsync(MigratedTemplateName, SeededTemplateName);
        await seed(ConnectionStringFor(SeededTemplateName));
        await SealTemplateAsync(SeededTemplateName);
        _seededTemplateReady = true;
    }

    /// <summary>A new database already holding the roles and users every <see cref="WebTestFixture"/> expects.</summary>
    internal static async Task<string> CreateSeededDatabaseAsync()
    {
        RequireContainer();
        if (!_seededTemplateReady)
        {
            throw new InvalidOperationException("The seeded PostgreSQL template has not been prepared.");
        }

        var databaseName = NewDatabaseName();
        await CloneAsync(SeededTemplateName, databaseName);
        return TestConnectionStringFor(databaseName);
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
    /// Creates a database migrated to <paramref name="targetMigration"/> or, when
    /// omitted, the current migration. Tests use an explicit historical target to
    /// prove that a deployed installation can upgrade forward rather than only
    /// proving a brand-new database works; only the current schema is cloned.
    /// </summary>
    internal static async Task<string> CreateMigratedDatabaseAsync(
        string? targetMigration = null)
    {
        RequireContainer();
        var databaseName = NewDatabaseName();

        if (targetMigration is null)
        {
            await CloneAsync(MigratedTemplateName, databaseName);
            return TestConnectionStringFor(databaseName);
        }

        await CreateDatabaseAsync(databaseName);
        var connectionString = TestConnectionStringFor(databaseName);
        await MigrateAsync(connectionString, targetMigration);
        return connectionString;
    }

    private static void RequireContainer()
    {
        if (_container is null)
        {
            throw new InvalidOperationException(UnavailableReason);
        }
    }

    private static string NewDatabaseName() => $"fl_{Guid.NewGuid():N}";

    // Administrative and template connections are unpooled: a template must have
    // no session at all, idle or not, when it is copied.
    private static string AdminConnectionString => new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
    {
        Pooling = false
    }.ConnectionString;

    private static string ConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = databaseName }.ConnectionString;

    /// <summary>
    /// What tests and their hosts connect with. Pooled, because test classes run
    /// in parallel and opening a fresh socket per query exhausts the client's
    /// ephemeral ports (on Windows especially). Every database has its own pool,
    /// so idle connections are pruned within seconds rather than holding server
    /// slots for databases whose class has already finished.
    /// </summary>
    private static string TestConnectionStringFor(string databaseName) =>
        new NpgsqlConnectionStringBuilder(ConnectionStringFor(databaseName))
        {
            Pooling = true,
            ConnectionIdleLifetime = 2,
            ConnectionPruningInterval = 1
        }.ConnectionString;

    private static Task CreateDatabaseAsync(string databaseName) =>
        // The name is a generated GUID or a constant above, never caller input,
        // and PostgreSQL does not accept a parameter in this position.
        ExecuteAdminAsync($"CREATE DATABASE \"{databaseName}\"");

    private static async Task CloneAsync(string templateName, string databaseName)
    {
        await CloneGate.WaitAsync();
        try
        {
            await ExecuteAdminAsync($"CREATE DATABASE \"{databaseName}\" TEMPLATE \"{templateName}\"");
        }
        finally
        {
            CloneGate.Release();
        }
    }

    /// <summary>
    /// Marks a finished template read-only to the suite: refusing connections
    /// means no stray session can ever block a copy of it.
    /// </summary>
    private static Task SealTemplateAsync(string templateName) =>
        ExecuteAdminAsync($"ALTER DATABASE \"{templateName}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");

    private static async Task ExecuteAdminAsync(string commandText)
    {
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await using var command = admin.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString, string? targetMigration)
    {
        // Migrate before any host boots: Program.cs seeds roles and the bootstrap
        // administrator during startup and would fault against an empty database.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync(targetMigration);
    }
}
