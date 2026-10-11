using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// M2-0: the one catalog search orchestration used by the HTTP endpoints, progressive runs and later callers.
/// Behavior is the behavior the endpoints had when the logic lived there.
/// </summary>
[TestClass]
public sealed class CatalogSearchServiceTests
{
    private static BookCandidate Book(string provider, string id, string title, string author, string? description = null) =>
        new(provider, provider.ToUpperInvariant(), id, title, [author], description, null, null, [], []);

    private sealed class StubProvider(
        string id, Func<BookSearchQuery, CancellationToken, Task<BookCandidateSearchPage>> search) : IBookMetadataProvider
    {
        public string Id => id;

        public string DisplayName => id.ToUpperInvariant();

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) =>
            search(query, cancellationToken);

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken) =>
            Task.FromResult<BookCandidate?>(null);
    }

    private sealed class StubResolver(params IBookMetadataProvider[] providers) : IActiveMetadataProviderResolver
    {
        public Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IBookMetadataProvider>>(providers);

        public Task<IBookMetadataProvider?> FindActiveProviderAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(providers.FirstOrDefault(provider => provider.Id == providerId));
    }

    private static StubProvider Returning(string id, bool hasMore, params BookCandidate[] candidates) =>
        new(id, (_, _) => Task.FromResult(new BookCandidateSearchPage(candidates, hasMore)));

    private static StubProvider Throwing(string id, Exception exception) => new(id, (_, _) => throw exception);

    private static readonly BookSearchQuery Query = new("The Lantern Road");

    [TestMethod]
    public async Task ResultsFromEveryActiveProviderAreGroupedAndRankedExactlyAsTheGrouperDoes()
    {
        var one = Book("one", "a", "The Lantern Road", "Mara Quill", "A novel.");
        var two = Book("two", "b", "The Lantern Road", "Mara Quill", "A novel.");
        var service = new CatalogSearchService(new StubResolver(Returning("one", false, one), Returning("two", true, two)));

        var outcome = await service.SearchAsync(Query, CancellationToken.None);

        var expected = BookCandidateGrouper.GroupMatchingCandidates([one, two], Query.Text);
        CollectionAssert.AreEqual(
            expected.Select(candidate => (candidate.ProviderId, candidate.ExternalId)).ToArray(),
            outcome.Candidates.Select(candidate => (candidate.ProviderId, candidate.ExternalId)).ToArray());
        Assert.AreEqual(1, Query.Page);
        Assert.AreEqual(Query.Page, outcome.Page);
        Assert.IsTrue(outcome.HasMore, "Any successful provider with more pages makes the search continuable.");
        Assert.AreEqual("one,two", string.Join(",", outcome.Providers.Select(provider => provider.ProviderId)));
        Assert.IsTrue(outcome.Providers.All(provider => provider.Succeeded));
    }

    [TestMethod]
    public async Task AllProvidersAreSearchedConcurrentlyNotOneAfterAnother()
    {
        var started = 0;
        var bothStarted = new TaskCompletionSource();
        Func<BookSearchQuery, CancellationToken, Task<BookCandidateSearchPage>> search = async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return new BookCandidateSearchPage([], false);
        };
        var service = new CatalogSearchService(new StubResolver(new StubProvider("one", search), new StubProvider("two", search)));

        var outcome = await service.SearchAsync(Query, CancellationToken.None);

        Assert.AreEqual(2, outcome.Providers.Count, "A sequential fan-out would time out waiting for the other provider to start.");
    }

    [TestMethod]
    public async Task ADownSlowOrMalformedProviderIsReportedUnavailableWithoutFailingTheSearch()
    {
        var good = Book("good", "a", "The Lantern Road", "Mara Quill");
        var service = new CatalogSearchService(new StubResolver(
            Returning("good", false, good),
            Throwing("down", new HttpRequestException("refused")),
            Throwing("garbled", new JsonException("bad")),
            Throwing("slow", new TaskCanceledException("timeout"))));

        var outcome = await service.SearchAsync(Query, CancellationToken.None);

        Assert.AreEqual("good", outcome.Candidates.Single().ProviderId);
        var byId = outcome.ProviderResults.ToDictionary(result => result.ProviderId);
        Assert.IsTrue(byId["good"].Succeeded);
        Assert.AreEqual(ProviderSearchFailureKind.Unavailable, byId["down"].Failure!.Kind);
        Assert.AreEqual(ProviderSearchFailureKind.InvalidResponse, byId["garbled"].Failure!.Kind);
        Assert.AreEqual(ProviderSearchFailureKind.TimedOut, byId["slow"].Failure!.Kind);
        Assert.IsFalse(outcome.Providers.Single(provider => provider.ProviderId == "down").Succeeded);
        Assert.IsNull(byId["good"].Failure);
    }

    [TestMethod]
    public async Task OnlySuccessfulProvidersMakeASearchContinuable()
    {
        var service = new CatalogSearchService(new StubResolver(
            Returning("done", hasMore: false), Throwing("down", new HttpRequestException("refused"))));

        Assert.IsFalse((await service.SearchAsync(Query, CancellationToken.None)).HasMore);
    }

    [TestMethod]
    public async Task CallerCancellationAbortsTheSearchInsteadOfBeingReportedAsAProviderTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new CatalogSearchService(new StubResolver(new StubProvider("one", async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new BookCandidateSearchPage([], false);
        })));

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.SearchAsync(Query, cancellation.Token));
    }

    [TestMethod]
    public async Task AnUnexpectedProviderBugPropagatesUnlessTheCallerAsksForRunStyleTolerance()
    {
        var service = new CatalogSearchService(new StubResolver());
        var buggy = Throwing("buggy", new InvalidOperationException("bug"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.SearchProviderAsync(buggy, Query, tolerateUnexpectedFailures: false, CancellationToken.None));

        var tolerated = await service.SearchProviderAsync(buggy, Query, tolerateUnexpectedFailures: true, CancellationToken.None);
        Assert.IsFalse(tolerated.Succeeded);
        Assert.AreEqual(ProviderSearchFailureKind.Unexpected, tolerated.Failure!.Kind);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelling = new StubProvider("c", (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new BookCandidateSearchPage([], false)); });
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SearchProviderAsync(cancelling, Query, tolerateUnexpectedFailures: true, cancelled.Token));
    }

    [TestMethod]
    public void ComposingAProgressiveSnapshotKeepsCandidatesOfAProviderThatFailedOnALaterPage()
    {
        var candidate = Book("one", "a", "The Lantern Road", "Mara Quill");
        var service = new CatalogSearchService(new StubResolver());

        var outcome = service.Compose(
            [new ProviderSearchResult("one", "ONE", Succeeded: false, [candidate], HasMore: true)], new BookSearchQuery("The Lantern Road", 2));

        Assert.HasCount(1, outcome.Candidates);
        Assert.AreEqual(2, outcome.Page);
        Assert.IsFalse(outcome.HasMore, "A failed provider never offers another page.");
        Assert.IsFalse(outcome.Providers.Single().Succeeded);
    }

    [TestMethod]
    public async Task WithNoActiveProvidersTheOutcomeIsEmptyNotAnError()
    {
        var outcome = await new CatalogSearchService(new StubResolver()).SearchAsync(Query, CancellationToken.None);

        Assert.IsEmpty(outcome.Candidates);
        Assert.IsEmpty(outcome.Providers);
        Assert.IsFalse(outcome.HasMore);
    }

    [TestMethod]
    [DataRow("a", false)]
    [DataRow("ab", true)]
    [DataRow(null, false)]
    [DataRow("   ", false)]
    public void QueryTextMustBeAtLeastTwoCharacters(string? text, bool valid) =>
        Assert.AreEqual(valid, CatalogSearchLimits.IsValidQueryText(text));

    [TestMethod]
    public void QueryTextOverTwoHundredCharactersIsRejectedAtTheBoundary()
    {
        Assert.IsTrue(CatalogSearchLimits.IsValidQueryText(new string('x', 200)));
        Assert.IsFalse(CatalogSearchLimits.IsValidQueryText(new string('x', 201)));
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(100, true)]
    [DataRow(101, false)]
    public void PageMustBeWithinTheSupportedRange(int page, bool valid) =>
        Assert.AreEqual(valid, CatalogSearchLimits.IsValidPage(page));
}
