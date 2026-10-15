using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyLibrarian.Web.Tests;

// AUTOFIND 2b. Which search variants were tried for a request format and provider is remembered ("last tried at", not "done
// forever"): a scheduled recheck re-runs the whole list, a retry pass re-runs only the base search and variants that returned
// something, and the variant that found the chosen candidate is stored with it so a review pick starts with that search.
// A candidate below the confidence threshold also rebuilds the pool before the explicit human confirmation is applied.
// Demo Hobbit work: variants are base, other ISBN, "Middle-earth 0", "Tolkien, J. R. R.".

[TestClass]
public sealed class ExternalProviderSearchVariantMemoryEndpointTests
{
    private static async Task<SearchVariantAttempt[]> RowsAsync(FamilyLibrarianAppFactory factory, Guid requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var formatIds = await database.RequestFormats.AsNoTracking()
            .Where(format => format.RequestId == requestId).Select(format => format.Id).ToListAsync();
        return await database.SearchVariantAttempts.AsNoTracking()
            .Where(row => formatIds.Contains(row.RequestFormatId)).OrderBy(row => row.FirstTriedAtUtc).ThenBy(row => row.VariantKind).ToArrayAsync();
    }

    [TestMethod]
    public async Task ARecheckWritesOneRowPerVariantAndMarksTheVariantThatFoundTheCandidate()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0" ? [VariantSearchProviderClient.Hobbit("series-hit", withAuthor: true)] : []);
        SearchVariantAttempt[] rows = [];

        await VariantScenario.RunAsync(fixture, client, "variant-memory-source", afterwards: async (factory, _, request) =>
            rows = await RowsAsync(factory, request.Id));

        CollectionAssert.AreEqual(
            new List<string> { "Base", "Isbn", "SeriesPosition" }, rows.Select(row => row.VariantKind).ToList());
        Assert.IsTrue(rows.All(row => row.TimesTried == 1 && row.ProviderId == "variant-memory-source"));
        CollectionAssert.AreEqual(new List<int> { 0, 0, 1 }, rows.Select(row => row.LastResultCount).ToList());
        Assert.AreEqual("MIDDLE-EARTH#0", rows[2].VariantKey);
        Assert.IsNull(rows[0].FoundSelectedAtUtc);
        Assert.IsNotNull(rows[2].FoundSelectedAtUtc, "The variant that found the downloaded candidate is marked.");
    }

    [TestMethod]
    public async Task ASecondScheduledPassRunsTheWholeListAgainAndCountsIt()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var client = new VariantSearchProviderClient(_ => []);
        SearchVariantAttempt[] rows = [];

        await VariantScenario.RunAsync(fixture, client, "variant-memory-twice", passes: 2,
            beforePass: async (factory, _, pass) =>
            {
                if (pass == 0) return;
                await using var scope = factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(
                    "UPDATE acquisition.provider_attempts SET next_eligible_check_at_utc = now() - interval '1 minute'");
            },
            afterwards: async (factory, _, request) => rows = await RowsAsync(factory, request.Id));

        Assert.HasCount(4, rows);
        Assert.IsTrue(rows.All(row => row.TimesTried == 2), string.Join(",", rows.Select(row => row.TimesTried)));
        Assert.HasCount(8, client.SearchedTitles);
    }

    [TestMethod]
    public async Task ARetryPassReRunsOnlyTheBaseSearchAndVariantsThatReturnedSomething()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        // Only the series search returns anything (an unrelated book), so only it is "productive".
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0"
                ? [VariantSearchProviderClient.Unrelated("noise-1")]
                : []);
        int searchesBeforeRetry = 0;

        await VariantScenario.RunAsync(fixture, client, "variant-memory-retry", passes: 2,
            beforePass: async (factory, request, pass) =>
            {
                if (pass == 0) return;
                searchesBeforeRetry = client.SearchedTitles.Count;
                // The retry loop's own row: the latest attempt is Retrying and due now.
                await using var scope = factory.Services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var formatId = request.Formats.Single().FormatId;
                database.ProviderAttempts.Add(new ProviderAttempt(
                    request.Id, formatId, "variant-memory-retry", ProviderAttemptOutcome.Retrying, "Advancing.",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1)));
                await database.SaveChangesAsync();
            });

        Assert.AreEqual(4, searchesBeforeRetry, "The first, scheduled pass ran the whole list.");
        Assert.AreEqual(2, client.SearchedTitles.Count - searchesBeforeRetry, "The retry pass ran the base search and the productive series variant only.");
    }

    [TestMethod]
    public async Task AReviewCandidateStoresItsVariantAndAPickRevalidatesItBeforeRebuildingConfidence()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0" ? [VariantSearchProviderClient.Hobbit("series-hit", withAuthor: false)] : []);
        string? stored = null;
        int searchesBeforePick = 0;
        HttpResponseMessage? pick = null;

        await VariantScenario.RunAsync(fixture, client, "variant-memory-review", providerConfidence: "Strict",
            afterwards: async (factory, admin, request) =>
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var candidate = await database.RequestReviewCandidates.AsNoTracking().SingleAsync(item => item.RequestId == request.Id);
                stored = candidate.SearchVariant;
                var current = await database.BookRequests.AsNoTracking().SingleAsync(item => item.Id == request.Id);
                searchesBeforePick = client.SearchedTitles.Count;
                pick = await admin.PostAsJsonAsync(
                    $"/api/v1/admin/requests/{request.Id}/needs-review/resolve",
                    new ResolveNeedsReviewRequest(candidate.Id, current.Version));
            });

        Assert.AreEqual("SeriesPosition:MIDDLE-EARTH#0", stored);
        Assert.IsTrue(pick!.IsSuccessStatusCode, pick.StatusCode.ToString());
        Assert.AreEqual("series-hit", client.SubmittedCandidates.Single());
        CollectionAssert.AreEqual(
            new List<string> { "Middle-earth 0", "The Hobbit", "The Hobbit", "The Hobbit" },
            client.SearchedTitles.Skip(searchesBeforePick).ToList(),
            "Revalidation starts with the stored variant, then rebuilds the confidence pool from the other variants for this uncorroborated candidate.");
    }

    [TestMethod]
    public async Task TheAdminSummarySaysHowManyWaysWereSearchedAndWhichFoundIt()
    {
        await using var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0" ? [VariantSearchProviderClient.Hobbit("series-hit", withAuthor: true)] : []);
        SearchVariantSummaryResponse[]? summary = null;

        await VariantScenario.RunAsync(fixture, client, "variant-memory-summary", afterwards: async (_, admin, request) =>
            summary = await admin.GetFromJsonAsync<SearchVariantSummaryResponse[]>($"/api/v1/admin/requests/{request.Id}/search-variants"));

        var line = summary!.Single();
        Assert.AreEqual("variant-memory-summary", line.ProviderId);
        Assert.AreEqual(3, line.DistinctVariants);
        Assert.AreEqual("SeriesPosition", line.FoundByKind);
        StringAssert.StartsWith(line.Summary, "Searched 3 way(s) at variant-memory-summary, last on ");
        StringAssert.EndsWith(line.Summary, "; found by SeriesPosition");
    }
}
