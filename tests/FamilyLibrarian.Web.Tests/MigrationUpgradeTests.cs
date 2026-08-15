using FamilyLibrarian.Infrastructure.Integrations;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    // HEAD immediately before the M8 GeneralizeProviderModel migration: the last
    // schema where a stored provider credential still lives in
    // app.metadata_provider_settings under its pre-rename name.
    private const string PreProviderRenameMigration = "20260814193405_AddUserWorkFeedback";

    // HEAD immediately before the M9 AddAcquisitionStaging migration: M8 is
    // applied (the provider rename), but the acquisition schema does not exist yet.
    private const string PreAcquisitionSchemaMigration = "20260815144417_GeneralizeProviderModel";

    // HEAD immediately before the M10 AddSecurityPipeline migration: M9 is
    // applied (the acquisition schema), but the security schema does not exist yet.
    private const string PreSecuritySchemaMigration = "20260815150647_AddAcquisitionStaging";

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

    /// <summary>
    /// Proves the M8 GeneralizeProviderModel migration moves
    /// <c>app.metadata_provider_settings</c> into
    /// <c>providers.provider_settings</c> as a rename, not a drop/recreate — a
    /// stored, encrypted provider credential written under the old table must
    /// still exist, and still decrypt, after upgrading.
    /// </summary>
    [TestMethod]
    public async Task AStoredProviderCredentialSurvivesTheGeneralizeProviderModelMigration()
    {
        if (PostgresFixture.UnavailableReason is not null)
        {
            Assert.Inconclusive(PostgresFixture.UnavailableReason);
        }

        var dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())));
        var protector = new DataProtectionCredentialProtector(dataProtection);
        const string providerId = "googlebooks";
        const string plaintext = "pre-rename-provider-key";
        var ciphertext = protector.Protect(providerId, plaintext);
        var createdAtUtc = DateTimeOffset.UtcNow;

        var connectionString = await PostgresFixture.CreateMigratedDatabaseAsync(
            PreProviderRenameMigration);

        // Seed through raw SQL against the pre-rename table shape: the
        // MetadataProviderSetting CLR type no longer exists in this codebase
        // after the M8 rename, so EF cannot write to the historical table
        // directly.
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO app.metadata_provider_settings
                    (provider_id, is_enabled, protected_credential, credential_format_version,
                     credential_hint, credential_set_at_utc, created_at_utc, updated_at_utc)
                VALUES
                    (@providerId, true, @protectedCredential, @formatVersion,
                     @hint, @setAt, @createdAt, @createdAt)
                """;
            insert.Parameters.AddWithValue("providerId", providerId);
            insert.Parameters.AddWithValue("protectedCredential", ciphertext);
            insert.Parameters.AddWithValue("formatVersion", protector.FormatVersion);
            insert.Parameters.AddWithValue("hint", plaintext[^4..]);
            insert.Parameters.AddWithValue("setAt", createdAtUtc);
            insert.Parameters.AddWithValue("createdAt", createdAtUtc);
            await insert.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var database = new AppDbContext(options);
        await database.Database.MigrateAsync();

        var migrated = await database.ProviderSettings.SingleAsync(
            setting => setting.ProviderId == providerId);

        Assert.AreEqual(ciphertext, migrated.ProtectedCredential);
        Assert.AreEqual(
            plaintext,
            protector.Unprotect(providerId, migrated.ProtectedCredential!, migrated.CredentialFormatVersion));
    }

    /// <summary>
    /// Proves an M8 database (provider rename applied, no acquisition schema
    /// yet) upgrades cleanly to include M9's acquisition schema.
    /// </summary>
    [TestMethod]
    public async Task AnM8DatabaseUpgradesToIncludeTheAcquisitionSchema()
    {
        if (PostgresFixture.UnavailableReason is not null)
        {
            Assert.Inconclusive(PostgresFixture.UnavailableReason);
        }

        var connectionString = await PostgresFixture.CreateMigratedDatabaseAsync(
            PreAcquisitionSchemaMigration);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var database = new AppDbContext(options);
        Assert.IsTrue((await database.Database.GetPendingMigrationsAsync()).Any());

        await database.Database.MigrateAsync();

        Assert.HasCount(0, await database.Database.GetPendingMigrationsAsync());
        Assert.IsFalse(await database.MediaAssets.AnyAsync());
        Assert.IsFalse(await database.AcquisitionJobs.AnyAsync());
    }

    /// <summary>
    /// Proves an M9 database (acquisition schema applied, no security schema
    /// yet) upgrades cleanly to include M10's security schema.
    /// </summary>
    [TestMethod]
    public async Task AnM9DatabaseUpgradesToIncludeTheSecuritySchema()
    {
        if (PostgresFixture.UnavailableReason is not null)
        {
            Assert.Inconclusive(PostgresFixture.UnavailableReason);
        }

        var connectionString = await PostgresFixture.CreateMigratedDatabaseAsync(
            PreSecuritySchemaMigration);
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        await using var database = new AppDbContext(options);
        Assert.IsTrue((await database.Database.GetPendingMigrationsAsync()).Any());

        await database.Database.MigrateAsync();

        Assert.HasCount(0, await database.Database.GetPendingMigrationsAsync());
        Assert.IsFalse(await database.SecurityEvaluations.AnyAsync());
    }
}
