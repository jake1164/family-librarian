using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Feedback;
using FamilyLibrarian.Domain.Following;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Infrastructure.Publishing;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The additive catalog-evidence migration must not rewrite or reinterpret a single pre-existing value. Counts and ids are not
/// enough: every column of every row in every table is compared before and after, through a reversal and a reapplication,
/// on real PostgreSQL.
/// </summary>
[TestClass]
[TestCategory("Migration")]
public sealed class CatalogEvidenceMigrationTests
{
    private const string MigrationSuffix = "_AddCatalogObservationEvidence";
    private static readonly DateTimeOffset Past = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    /// <summary>The columns this migration adds to existing tables. Absent on the older schema, NULL on every legacy row.</summary>
    private static readonly Dictionary<string, string[]> AddedColumns = new()
    {
        ["catalog.works"] = ["version_class", "first_publication_precision", "first_publication_basis"],
        ["catalog.editions"] = ["identity_basis", "source_provider_id", "publication_precision"],
        ["catalog.work_authors"] = ["binding_basis"],
        ["catalog.series_entries"] = ["binding_basis", "position_state"]
    };

    /// <summary>What a later migration's back-fill changes: the filled column, and the row version of the updated rows.</summary>
    private static readonly (string Table, string[] Columns) BackfilledByLaterMigrations =
        ("identity.users", ["setup_wizard_seen_at_utc", "xmin_"]);

    private static readonly string[] ExpectedIsbns = ["9780000000002", "9780000000019", "9780000000026"];

    private static readonly string[] ExpectedAliases = ["Het Namesake Boek", "Namesake Book: A Variant"];

    private static readonly string[] AddedTables =["catalog.source_observations", "catalog.metadata_claims"];

    /// <summary>
    /// Tables that a migration newer than this one drops or creates. Reversing this migration also reverses those, so they exist
    /// on one side of the reversal only and are outside what this test compares.
    /// </summary>
    private static readonly string[] TablesChangedByLaterMigrations =
        ["providers.provider_catalogs", "conversation.settings", "communications.setup_guidance_settings"];

    /// <summary>
    /// Columns that a migration newer than this one adds. Reversing this migration removes them too; reapplying restores
    /// their defaults, so they are left out of the reversed comparison only.
    /// </summary>
    private static readonly Dictionary<string, string[]> ColumnsAddedByLaterMigrations = new()
    {
        ["identity.users"] =
        [
            "default_request_format", "setup_wizard_seen_at_utc", "setup_completed_steps", "setup_skipped_steps"
        ]
    };

    [TestMethod]
    public async Task EveryPreExistingValueSurvivesTheMigrationItsReversalAndItsReapplication()
    {
        var connection = await NewDatabaseAsync();
        var ids = await SeedLegacyAsync(connection);

        await using var database = Open(connection);
        var migrations = database.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(name => name.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.IsTrue(index > 0, "The evidence migration is part of the model.");

        var before = await DumpAsync(connection);
        var lookupBefore = await LookupAsync(connection, ids);
        Assert.IsTrue(before["catalog.works"].Count >= 4, "The legacy fixture seeded its Works.");

        // Reverse: the older schema has neither new table nor new column, and every old value is untouched.
        await database.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        foreach (var table in AddedTables)
        {
            Assert.IsNull(await ScalarAsync(connection, $"SELECT to_regclass('{table}')::text"), $"{table} is gone after Down.");
        }

        foreach (var (table, columns) in AddedColumns)
        {
            foreach (var column in columns)
            {
                Assert.AreEqual(0L, await ScalarAsync<long>(connection,
                    $"SELECT count(*) FROM information_schema.columns WHERE table_schema = '{table.Split('.')[0]}' " +
                    $"AND table_name = '{table.Split('.')[1]}' AND column_name = '{column}'"), $"{table}.{column} is gone after Down.");
            }
        }

        var older = await DumpAsync(connection);
        AssertSameRows(StripAdded(before), older, "after Down");

        // Reapply: identical to the starting point, with every new column NULL and both new tables empty.
        await database.Database.MigrateAsync();
        foreach (var table in AddedTables)
        {
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}"), $"{table} starts empty: nothing is back-filled.");
        }

        foreach (var (table, columns) in AddedColumns)
        {
            foreach (var column in columns)
            {
                Assert.AreEqual(0L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table} WHERE {column} IS NOT NULL"),
                    $"{table}.{column} is NULL on every legacy row.");
            }
        }

        var after = await DumpAsync(connection);
        // A later migration deliberately fills this in for every account that already exists.
        Assert.AreEqual(0L, await ScalarAsync<long>(connection,
            "SELECT count(*) FROM identity.users WHERE setup_wizard_seen_at_utc IS NULL"));
        AssertSameRows(
            Strip(before, BackfilledByLaterMigrations), Strip(after, BackfilledByLaterMigrations), "after reapply");
        Assert.IsFalse(database.Database.HasPendingModelChanges(), "The migration and snapshot match the model.");

        // The lookup every consumer trusts is byte-for-byte what it was, and is the legacy shape we expect.
        var lookupAfter = await LookupAsync(connection, ids);
        CollectionAssert.AreEqual(lookupBefore.ToArray(), lookupAfter.ToArray());
        var main = JsonNode.Parse(lookupAfter[0])!;
        Assert.AreEqual("The Namesake Book", main["Title"]!.GetValue<string>());
        Assert.AreEqual(string.Join("|", ExpectedIsbns), string.Join("|", main["Isbn13s"]!.AsArray().Select(node => node!.GetValue<string>()).Order(StringComparer.Ordinal)));
        Assert.AreEqual(string.Join("|", ExpectedAliases), string.Join("|", main["AlternateTitles"]!.AsArray().Select(node => node!.GetValue<string>()).Order(StringComparer.Ordinal)));
        Assert.AreEqual("1937-01-01", main["FirstPublicationDate"]!.GetValue<string>());
        Assert.IsNull(main["Language"], "The first-created edition has no language.");
        Assert.IsNull(main["Publisher"]);
    }

    [TestMethod]
    public async Task TheNewEvidenceTablesEnforceTheirKeysAndNeverFollowAProviderConfiguration()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var work = new Work("Keyed", null, null, null, PublicationStatus.Unknown, Past);
        database.Works.Add(work);
        await database.SaveChangesAsync();

        // Provider ids are history, not foreign keys: any text is accepted, including a provider that is not installed.
        var snapshot = new SourceObservation(work.Id, "removed-provider", "ext-1", new string('a', 64), "{\"title\":\"Keyed\"}", 1, Past);
        database.SourceObservations.Add(snapshot);
        var first = new MetadataClaim(work.Id, MetadataClaimField.SeriesPosition, "scope", "removed-provider", "ext-1", "{\"sort\":1}", new string('b', 64), Past);
        first.Select();
        database.MetadataClaims.Add(first);
        await database.SaveChangesAsync();

        // The same content again is a duplicate, whichever order it arrives in.
        await using (var duplicate = Open(connection))
        {
            duplicate.SourceObservations.Add(new SourceObservation(work.Id, "removed-provider", "ext-1", new string('a', 64), "{}", 1, Past));
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        // At most one selected claim per Work, field and scope.
        await using (var second = Open(connection))
        {
            var other = new MetadataClaim(work.Id, MetadataClaimField.SeriesPosition, "scope", "another", "ext-2", "{\"sort\":2}", new string('c', 64), Past);
            other.Select();
            second.MetadataClaims.Add(other);
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => second.SaveChangesAsync());
        }

        // A Work with evidence cannot be deleted from under it.
        await using (var deleting = Open(connection))
        {
            await Assert.ThrowsExactlyAsync<PostgresException>(async () =>
                await deleting.Database.ExecuteSqlRawAsync("DELETE FROM catalog.works WHERE id = {0}", work.Id));
        }
    }

    private static async Task<(Guid Main, Guid Retired, Guid Requested, Guid Lonely)> SeedLegacyAsync(string connection)
    {
        await using var database = Open(connection);
        var user = new AppUser { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), Email = "reader@example.test", DisplayName = "Reader" };
        database.Users.Add(user);

        // Two different people who share a name, and two series that share a name: legacy data the new code must never merge.
        var olderNamesake = new Author("A. Namesake", "Namesake, A.", Past.AddDays(-30));
        var newerNamesake = new Author("A. Namesake", null, Past);
        var series = new Series("The Namesake Cycle", SeriesStatus.Active, Past.AddDays(-20));
        var twin = new Series("The Namesake Cycle", SeriesStatus.Unknown, Past);
        database.Authors.AddRange(olderNamesake, newerNamesake);
        database.Series.AddRange(series, twin);

        // A year-only date normalised to 1 January (legacy), NULL-language editions, a repeated title alias.
        var main = new Work("The Namesake Book", "A description.", "https://example.test/cover.jpg", new DateOnly(1937, 1, 1), PublicationStatus.Published, Past);
        main.SetCatalogLength(321, null, "demo", Past);
        main.AddAuthor(olderNamesake, 0, "Author");
        main.AddEdition(new Edition(main.Id, "The Namesake Book", EditionFormat.Hardcover, "9780000000002", new DateOnly(1937, 9, 21), Past));
        main.AddEdition(new Edition(main.Id, "Namesake Book: A Variant", EditionFormat.Paperback, "9780000000019", null, Past.AddMinutes(1)));
        main.AddEdition(new Edition(main.Id, "Het Namesake Boek", EditionFormat.Ebook, "9780000000026", new DateOnly(2001, 5, 6), Past.AddMinutes(2)));
        database.Works.Add(main);
        database.ExternalReferences.Add(new ExternalReference("openlibrary", ExternalReferenceEntityType.Work, main.Id, "OL1W", Past));
        database.ExternalReferences.Add(new ExternalReference("removed-provider", ExternalReferenceEntityType.Work, main.Id, "gone-1", Past.AddHours(1)));

        // A conflicting position pair: two Works claiming position 1 in the same series.
        var sibling = new Work("Second Namesake", null, null, null, PublicationStatus.Unknown, Past.AddDays(1));
        sibling.AddAuthor(newerNamesake, 0);
        main.AddSeriesEntry(new SeriesEntry(series, main, "1", 1m, true, Past));
        sibling.AddSeriesEntry(new SeriesEntry(series, sibling, "1", 1m, false, Past.AddDays(1)));
        database.Works.Add(sibling);

        // A retired Work that let go of its unique rows, with its cancelled request still pointing at it.
        var retired = new Work("Retired Book", null, null, null, PublicationStatus.Unknown, Past);
        retired.Retire(Past.AddDays(2));
        database.Works.Add(retired);
        var cancelled = new BookRequest(user.Id, retired.Id, [RequestMediaType.Ebook], null, Past);
        cancelled.Withdraw(user.Id, Past);
        database.BookRequests.Add(cancelled);

        // An active request, an asset, and feedback.
        var requested = new Work("Requested Book", null, null, null, PublicationStatus.Unknown, Past);
        database.Works.Add(requested);
        var live = new BookRequest(user.Id, requested.Id, [RequestMediaType.Ebook], null, Past);
        database.BookRequests.Add(live);
        database.MediaAssets.Add(new MediaAsset(
            requested.Id, null, RequestMediaType.Ebook, ".epub", "book.epub", "stored.epub", 1000, new string('d', 64),
            "application/epub+zip", live.Formats.Single().Id, null, Past));
        database.UserWorkFeedback.Add(new UserWorkFeedback(user.Id, requested.Id, new DateOnly(2026, 2, 1), Past));

        // A follower with an unread notification and another with a read one.
        database.Follows.Add(new Follow(user.Id, FollowSubjectType.Author, olderNamesake.Id, Past));
        database.Follows.Add(new Follow(user.Id, FollowSubjectType.Series, series.Id, Past));
        var unread = new NotificationEvent(NotificationAudience.SingleUser, user.Id, "catalog.author_new_work", NotificationSeverity.Info, "Unread", "detail", "Work", main.Id.ToString(), Past);
        var read = new NotificationEvent(NotificationAudience.SingleUser, user.Id, "catalog.series_new_entry", NotificationSeverity.Info, "Read", null, "Work", sibling.Id.ToString(), Past);
        database.NotificationEvents.AddRange(unread, read);
        var receipt = new NotificationReceipt(read.Id, user.Id);
        receipt.MarkRead(Past.AddHours(3));
        database.NotificationReceipts.Add(receipt);

        // A work nobody wrote anything about.
        var lonely = new Work("Lonely Book", null, null, null, PublicationStatus.Forthcoming, Past);
        database.Works.Add(lonely);
        await database.SaveChangesAsync();

        // Legacy language/publisher values that already exist in the schema must survive too.
        await database.Database.ExecuteSqlRawAsync(
            "UPDATE catalog.editions SET language = 'nl', publisher = 'Uitgever' WHERE isbn13 = '9780000000026'");
        return (main.Id, retired.Id, requested.Id, lonely.Id);
    }

    private static async Task<List<string>> LookupAsync(string connection, (Guid Main, Guid Retired, Guid Requested, Guid Lonely) ids)
    {
        await using var database = Open(connection);
        var lookup = new WorkLookup(database);
        var output = new List<string>();
        foreach (var id in new[] { ids.Main, ids.Retired, ids.Requested, ids.Lonely })
        {
            output.Add(JsonSerializer.Serialize(await lookup.FindAsync(id, CancellationToken.None)));
        }

        return output;
    }

    /// <summary>Every column of every row of every table, keyed by "schema.table"; the row version (xmin) is part of the row.</summary>
    private static async Task<Dictionary<string, List<string>>> DumpAsync(string connection)
    {
        await using var data = new NpgsqlConnection(connection);
        await data.OpenAsync();
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT quote_ident(schemaname) || '.' || quote_ident(tablename) FROM pg_catalog.pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema') " +
            "AND tablename <> '__EFMigrationsHistory' AND schemaname || '.' || tablename <> ALL (@later) ORDER BY 1", data))
        {
            list.Parameters.AddWithValue("later", TablesChangedByLaterMigrations);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var dump = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = new List<string>();
            await using var command = new NpgsqlCommand(
                $"SELECT (to_jsonb(t.*) || jsonb_build_object('xmin_', t.xmin::text))::text FROM {table} t", data);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(Canonical(reader.GetString(0)));
            }

            rows.Sort(StringComparer.Ordinal);
            dump[table] = rows;
        }

        return dump;
    }

    private static Dictionary<string, List<string>> StripAdded(Dictionary<string, List<string>> dump)
    {
        var stripped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (table, rows) in dump)
        {
            if (AddedTables.Contains(table, StringComparer.Ordinal))
            {
                continue;
            }

            string[] columns =
                [.. AddedColumns.GetValueOrDefault(table, []), .. ColumnsAddedByLaterMigrations.GetValueOrDefault(table, [])];
            if (columns.Length == 0)
            {
                stripped[table] = rows;
                continue;
            }

            stripped[table] = rows.Select(row =>
            {
                var node = JsonNode.Parse(row)!.AsObject();
                foreach (var column in columns)
                {
                    node.Remove(column);
                }

                return Canonical(node.ToJsonString());
            }).Order(StringComparer.Ordinal).ToList();
        }

        return stripped;
    }

    private static Dictionary<string, List<string>> Strip(
        Dictionary<string, List<string>> dump, (string Table, string[] Columns) removed) =>
        dump.ToDictionary(
            entry => entry.Key,
            entry => entry.Key != removed.Table
                ? entry.Value
                : entry.Value.Select(row =>
                {
                    var node = JsonNode.Parse(row)!.AsObject();
                    foreach (var column in removed.Columns)
                    {
                        node.Remove(column);
                    }

                    return Canonical(node.ToJsonString());
                }).Order(StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

    private static string Canonical(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var sorted = new JsonObject();
        foreach (var property in node.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            sorted[property.Key] = property.Value?.DeepClone();
        }

        return sorted.ToJsonString();
    }

    private static void AssertSameRows(Dictionary<string, List<string>> expected, Dictionary<string, List<string>> actual, string when)
    {
        // The two new tables only exist on one side of a reversal; everything else must match table for table.
        var tables = expected.Keys.Where(table => !AddedTables.Contains(table, StringComparer.Ordinal)).ToArray();
        foreach (var table in tables)
        {
            Assert.IsTrue(actual.ContainsKey(table), $"{table} exists {when}.");
            CollectionAssert.AreEqual(expected[table], actual[table], $"{table} rows are identical {when}.");
        }

        CollectionAssert.AreEquivalent(
            tables,
            actual.Keys.Where(table => !AddedTables.Contains(table, StringComparer.Ordinal)).ToArray(),
            $"No table appeared or vanished {when}.");
    }

    private static async Task<object?> ScalarAsync(string connection, string sql)
    {
        await using var data = new NpgsqlConnection(connection);
        await data.OpenAsync();
        await using var command = new NpgsqlCommand(sql, data);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<T> ScalarAsync<T>(string connection, string sql) => (T)(await ScalarAsync(connection, sql))!;

    private static AppDbContext Open(string connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options);

    private static Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync();
    }
}
