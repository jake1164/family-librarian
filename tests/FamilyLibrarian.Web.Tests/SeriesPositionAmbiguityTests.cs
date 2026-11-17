using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Chat's series lookup read a series by its normalized name with a single-result query, so two saved series that share a name made
/// every lookup involving that name throw. A name matching several saved series must be an explicit, answerable ambiguity: never a
/// failure, never the oldest namesake, and never a silent merge of two series into one.
/// </summary>
[TestClass]
public sealed class SeriesPositionAmbiguityTests
{
    private static readonly DateTimeOffset Past = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task TwoSavedSeriesWithOneNameAreAnExplicitAmbiguityNotAFailureOrAChoice()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        await SeedNamesakesAsync(database);
        var before = await RowSnapshotAsync(connection);

        // The defect this closes: the single-result lookup the lookup used to call cannot answer for a repeated name.
        var repository = new CatalogRepository(database);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await repository.FindSeriesByNormalizedNameAsync("the namesake cycle", CancellationToken.None));

        var service = new SeriesPositionService(new NoSearch(), repository);
        foreach (var position in new decimal?[] { 1m, null })
        {
            var result = await service.FindAsync("The Namesake Cycle", position, afterTitle: null, maxBooks: 5, CancellationToken.None);

            Assert.AreEqual(SeriesLookupKind.AmbiguousSeries, result.Kind);
            Assert.IsEmpty(result.Books, "No book is offered from either namesake.");
            Assert.HasCount(2, result.AmbiguousSeriesNames);
            Assert.IsTrue(result.AmbiguousSeriesNames.Any(name => name.Contains("Older Opening", StringComparison.Ordinal)));
            Assert.IsTrue(result.AmbiguousSeriesNames.Any(name => name.Contains("Newer Opening", StringComparison.Ordinal)));
        }

        // The "after" form asks the same question and gets the same answer.
        var after = await service.FindAsync("The Namesake Cycle", position: null, afterTitle: "Older Opening", maxBooks: 5, CancellationToken.None);
        Assert.AreEqual(SeriesLookupKind.AmbiguousSeries, after.Kind);

        Assert.AreEqual(before, await RowSnapshotAsync(connection), "A read-only lookup writes nothing.");
    }

    [TestMethod]
    public async Task ASeriesNamedByDifferentOrderLabelsKeepsItsOrderLabelsAndOneSeriesStillAnswers()
    {
        var connection = await NewDatabaseAsync();
        await using var database = Open(connection);
        var publication = new Series("The Expanse", SeriesStatus.Unknown, Past);
        var chronological = new Series("The Expanse (Chronological)", SeriesStatus.Unknown, Past);
        database.Series.AddRange(publication, chronological);
        var leviathan = await AddMemberAsync(database, "Leviathan Wakes", publication, "1", 1m, "ex-1");
        await AddMemberAsync(database, "The Churn", chronological, "1", 1m, "ex-2");
        _ = leviathan;

        var service = new SeriesPositionService(new NoSearch(), new CatalogRepository(database));
        var result = await service.FindAsync("The Expanse", position: 1m, afterTitle: null, maxBooks: 5, CancellationToken.None);

        // Distinct names are distinct rows, so no ambiguity is invented; the scope labels were never flattened away.
        Assert.AreEqual(SeriesLookupKind.Found, result.Kind);
        Assert.AreEqual("Leviathan Wakes", result.Books.Single().Title);
        Assert.AreEqual("The Expanse", result.Books.Single().SeriesName);
    }

    private static async Task SeedNamesakesAsync(AppDbContext database)
    {
        var older = new Series("The Namesake Cycle", SeriesStatus.Unknown, Past);
        var newer = new Series("The Namesake Cycle", SeriesStatus.Unknown, Past.AddDays(1));
        database.Series.AddRange(older, newer);
        await AddMemberAsync(database, "Older Opening", older, "1", 1m, "ns-1");
        await AddMemberAsync(database, "Newer Opening", newer, "1", 1m, "ns-2");
    }

    private static async Task<Work> AddMemberAsync(AppDbContext database, string title, Series series, string label, decimal sort, string externalId)
    {
        var work = new Work(title, null, null, null, PublicationStatus.Unknown, Past);
        work.AddSeriesEntry(new SeriesEntry(series, work, label, sort, true, Past));
        database.Works.Add(work);
        database.ExternalReferences.Add(new ExternalReference("example-source", ExternalReferenceEntityType.Work, work.Id, externalId, Past));
        await database.SaveChangesAsync();
        return work;
    }

    private static async Task<string> RowSnapshotAsync(string connection)
    {
        await using var data = new Npgsql.NpgsqlConnection(connection);
        await data.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT (SELECT string_agg(to_jsonb(w)::text || w.xmin::text, '|' ORDER BY w.id) FROM catalog.works w) || " +
            "(SELECT string_agg(to_jsonb(s)::text || s.xmin::text, '|' ORDER BY s.id) FROM catalog.series s) || " +
            "(SELECT string_agg(to_jsonb(e)::text || e.xmin::text, '|' ORDER BY e.id) FROM catalog.series_entries e)", data);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static AppDbContext Open(string connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options);

    private static Task<string> NewDatabaseAsync()
    {
        if (PostgresFixture.UnavailableReason is not null) Assert.Inconclusive(PostgresFixture.UnavailableReason);
        return PostgresFixture.CreateMigratedDatabaseAsync();
    }

    private sealed class NoSearch : ICatalogSearchService
    {
        public Task<CatalogSearchOutcome> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogSearchOutcome([], [], 1, false, []));

        public Task<ProviderSearchResult> SearchProviderAsync(
            IBookMetadataProvider provider, BookSearchQuery query, bool tolerateUnexpectedFailures, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public CatalogSearchOutcome Compose(IReadOnlyList<ProviderSearchResult> providerResults, BookSearchQuery query) =>
            throw new NotSupportedException();
    }
}
