using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Verifies an installation already running the request workflow can advance to
/// the checked-in current model without rebuilding its database from scratch.
/// </summary>
[TestClass]
public sealed class MigrationUpgradeTests
{
    // This is the schema after M5 and before the M5.5 account/invitation
    // migrations. It is a meaningful historical deployment state: requests
    // already exist and must remain upgradeable as the vertical slice grows.
    private const string RequestWorkflowMigration = "20260813112849_AddBookRequests";

    [TestMethod]
    public async Task ARequestWorkflowDatabaseUpgradesToTheCurrentSchema()
    {
        if (PostgresFixture.UnavailableReason is not null)
        {
            Assert.Inconclusive(PostgresFixture.UnavailableReason);
        }

        var connectionString = await PostgresFixture.CreateMigratedDatabaseAsync(
            RequestWorkflowMigration);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var database = new AppDbContext(options);
        var before = await database.Database.GetAppliedMigrationsAsync();
        CollectionAssert.Contains(before.ToArray(), RequestWorkflowMigration);
        Assert.IsTrue((await database.Database.GetPendingMigrationsAsync()).Any());

        // EF Core's migration lock/transaction management belongs to MigrateAsync;
        // do not wrap this call in an application transaction.
        await database.Database.MigrateAsync();

        var pending = await database.Database.GetPendingMigrationsAsync();
        Assert.HasCount(0, pending);

        // This query exercises an M5.5 table added after the historical target.
        // It makes a missing schema/table failure explicit instead of inferring
        // upgrade success only from migration-history rows.
        Assert.IsFalse(await database.Invitations.AnyAsync());
    }
}
