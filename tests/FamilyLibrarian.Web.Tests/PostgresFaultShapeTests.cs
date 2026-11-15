using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Evidence, not assumption: the exact exception types and SQLSTATEs that the pinned EF Core 10.0.14 / Npgsql 10.0.3 raise at
/// each boundary the catalog write scope classifies (lock wait, save, commit). The scope's retry/Busy/unknown-commit rules are
/// only as good as these shapes, so a library upgrade that changes one fails here first. Every case runs against real PostgreSQL.
/// </summary>
[TestClass]
public sealed class PostgresFaultShapeTests
{
    private static readonly DateTimeOffset Past = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    private static async Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return await PostgresFixture.CreateMigratedDatabaseAsync();
    }

    private static AppDbContext Open(string connection) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connection, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);

    private static Work WorkWithIsbn(string isbn)
    {
        var work = new Work($"Shape {Guid.NewGuid():N}", null, null, null, PublicationStatus.Published, Past);
        work.AddEdition(new Edition(work.Id, "Shape", EditionFormat.Ebook, isbn, null, Past));
        return work;
    }

    [TestMethod]
    public async Task ASaveThatBreaksAUniqueIndexIsADbUpdateExceptionWrappingPostgres23505()
    {
        var connection = await NewDatabaseAsync();
        await using (var first = Open(connection))
        {
            first.Works.Add(WorkWithIsbn("9780000900011"));
            await first.SaveChangesAsync();
        }

        await using var second = Open(connection);
        second.Works.Add(WorkWithIsbn("9780000900011"));
        var thrown = await Assert.ThrowsExactlyAsync<DbUpdateException>(async () => await second.SaveChangesAsync());

        var postgres = Assert.IsInstanceOfType<PostgresException>(thrown.InnerException);
        Assert.AreEqual(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        TestContext.WriteLine($"save/unique: {thrown.GetType().FullName} -> {postgres.GetType().FullName} {postgres.SqlState}");
    }

    [TestMethod]
    public async Task AStaleXminOnSaveIsADbUpdateConcurrencyException()
    {
        var connection = await NewDatabaseAsync();
        Guid id;
        await using (var seed = Open(connection))
        {
            var work = new Work($"Stale {Guid.NewGuid():N}", null, null, null, PublicationStatus.Published, Past);
            seed.Works.Add(work);
            await seed.SaveChangesAsync();
            id = work.Id;
        }

        await using var winner = Open(connection);
        await using var loser = Open(connection);
        var winning = await winner.Works.SingleAsync(work => work.Id == id);
        var losing = await loser.Works.SingleAsync(work => work.Id == id);
        winning.SetCatalogLength(100, null, "winner", Past);
        await winner.SaveChangesAsync();
        losing.SetCatalogLength(200, null, "loser", Past);

        var thrown = await Assert.ThrowsExactlyAsync<DbUpdateConcurrencyException>(async () => await loser.SaveChangesAsync());
        TestContext.WriteLine($"save/xmin: {thrown.GetType().FullName}");
    }

    [TestMethod]
    public async Task AnExpiredLockTimeoutOnTheAdvisoryLockIsAPostgresExceptionWith55P03()
    {
        var connection = await NewDatabaseAsync();
        await using var holder = Open(connection);
        await using var held = await CatalogWriteLockForTest.BeginAsync(holder);
        await using var waiter = Open(connection);
        await using var waiting = await waiter.Database.BeginTransactionAsync();
        await waiter.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'");

        var thrown = await Assert.ThrowsAsync<Exception>(async () =>
            await waiter.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({831705}, {0})"));

        var postgres = Assert.IsInstanceOfType<PostgresException>(thrown);
        Assert.AreEqual(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
        TestContext.WriteLine($"lock/timeout: {thrown.GetType().FullName} {postgres.SqlState}");
    }

    [TestMethod]
    public async Task ADeadlockIsAPostgresExceptionWith40P01OnExactlyOneParticipant()
    {
        var connection = await NewDatabaseAsync();
        await using var one = new NpgsqlConnection(connection);
        await using var two = new NpgsqlConnection(connection);
        await one.OpenAsync();
        await two.OpenAsync();
        await using var txOne = await one.BeginTransactionAsync();
        await using var txTwo = await two.BeginTransactionAsync();
        await ExecuteAsync(one, "SELECT pg_advisory_xact_lock(7001, 1)");
        await ExecuteAsync(two, "SELECT pg_advisory_xact_lock(7001, 2)");

        var first = Capture(() => ExecuteAsync(one, "SELECT pg_advisory_xact_lock(7001, 2)"));
        var second = Capture(() => ExecuteAsync(two, "SELECT pg_advisory_xact_lock(7001, 1)"));
        var errors = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        var victims = errors.OfType<PostgresException>().ToArray();

        // PostgreSQL aborts one participant; the survivor proceeds once the victim's transaction ends.
        Assert.IsGreaterThanOrEqualTo(1, victims.Length, "A deadlock victim was chosen.");
        Assert.IsTrue(victims.All(victim => victim.SqlState == PostgresErrorCodes.DeadlockDetected));
        TestContext.WriteLine($"deadlock: {victims[0].GetType().FullName} {victims[0].SqlState} (victims: {victims.Length})");
    }

    [TestMethod]
    public async Task ASerializationConflictIsAPostgresExceptionWith40001()
    {
        var connection = await NewDatabaseAsync();
        await using (var setup = new NpgsqlConnection(connection))
        {
            await setup.OpenAsync();
            await ExecuteAsync(setup, "CREATE TABLE fl_shape_probe (id int PRIMARY KEY, v int NOT NULL); INSERT INTO fl_shape_probe VALUES (1, 0), (2, 0)");
        }

        await using var one = new NpgsqlConnection(connection);
        await using var two = new NpgsqlConnection(connection);
        await one.OpenAsync();
        await two.OpenAsync();
        await using var txOne = await one.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await using var txTwo = await two.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        // Write skew: each reads the whole set and writes the row the other read.
        await ExecuteAsync(one, "SELECT sum(v) FROM fl_shape_probe; UPDATE fl_shape_probe SET v = 1 WHERE id = 1");
        await ExecuteAsync(two, "SELECT sum(v) FROM fl_shape_probe; UPDATE fl_shape_probe SET v = 1 WHERE id = 2");
        await txOne.CommitAsync();

        var thrown = await Assert.ThrowsAsync<PostgresException>(async () => await txTwo.CommitAsync());
        Assert.AreEqual(PostgresErrorCodes.SerializationFailure, thrown.SqlState);
        TestContext.WriteLine($"commit/serialization: {thrown.GetType().FullName} {thrown.SqlState}");
    }

    [TestMethod]
    public async Task ACommitAfterTheBackendWasTerminatedFailsAsAPostgresSessionErrorOrATransportError()
    {
        var connection = await NewDatabaseAsync();
        await using var db = Open(connection);
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Works.Add(WorkWithIsbn("9780000900028"));
        await db.SaveChangesAsync();
        var pid = await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString))
        {
            await admin.OpenAsync();
            await using var kill = admin.CreateCommand();
            kill.CommandText = "SELECT pg_terminate_backend(@pid)";
            kill.Parameters.AddWithValue("pid", pid);
            await kill.ExecuteScalarAsync();
        }

        var thrown = await Assert.ThrowsAsync<Exception>(async () => await transaction.CommitAsync());

        // Observed on Npgsql 10.0.3 / PostgreSQL 17: the server's termination notice reaches the client as a PostgresException
        // (admin_shutdown, 57P01), not as an I/O error, so the scope classifies the "session" SQLSTATE classes as unknown-commit too.
        var postgres = Assert.IsInstanceOfType<PostgresException>(thrown);
        Assert.AreEqual(PostgresErrorCodes.AdminShutdown, postgres.SqlState);
        TestContext.WriteLine($"commit/terminated: {thrown.GetType().FullName} {postgres.SqlState}");
        await using var verify = Open(connection);
        Assert.AreEqual(0, await verify.Editions.CountAsync(edition => edition.Isbn13 == "9780000900028"), "The terminated transaction did not commit.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    /// <summary>The production lock statement, run directly, so the lock-wait shape is observed on the same SQL the scope sends.</summary>
    private static class CatalogWriteLockForTest
    {
        public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginAsync(AppDbContext database)
        {
            var transaction = await database.Database.BeginTransactionAsync();
            await database.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({831705}, {0})");
            return transaction;
        }
    }
}
