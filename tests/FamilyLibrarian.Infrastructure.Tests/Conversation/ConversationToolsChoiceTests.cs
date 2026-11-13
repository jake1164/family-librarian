using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// CHAT-ANSWER-1: the real <see cref="ConversationBookTools"/> remembers which series the catalog gave each offered book, and
/// checks the library for an offered book by its own catalog record instead of a text search that may rank another record first.
/// All books here are invented.
/// </summary>
[TestClass]
public sealed class ConversationToolsChoiceTests
{
    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.NewGuid();

        public string? DisplayName => null;
    }

    private sealed class CountingSearch(CatalogSearchOutcome outcome) : ICatalogSearchService
    {
        public int Calls { get; private set; }

        public Task<CatalogSearchOutcome> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(outcome);
        }

        public Task<ProviderSearchResult> SearchProviderAsync(
            IBookMetadataProvider provider, BookSearchQuery query, bool tolerateUnexpectedFailures, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public CatalogSearchOutcome Compose(IReadOnlyList<ProviderSearchResult> providerResults, BookSearchQuery query) => throw new NotSupportedException();
    }

    private sealed class RecordingAvailability : ICandidateAvailabilityService
    {
        public List<BookIdentity> Identities { get; } = [];

        public Task<CandidateAvailabilityResult> GetAvailabilityAsync(BookIdentity identity, CancellationToken cancellationToken)
        {
            Identities.Add(identity);
            return Task.FromResult(new CandidateAvailabilityResult([], []));
        }

        public IAsyncEnumerable<CandidateAvailabilityUpdate> GetAvailabilityUpdatesAsync(BookIdentity identity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProvider(Func<string, BookCandidate?> details) : IBookMetadataProvider
    {
        public List<string> DetailsRequested { get; } = [];

        public string Id => "fake";

        public string DisplayName => "Fake catalog";

        public Task<BookCandidateSearchPage> SearchAsync(BookSearchQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BookCandidate?> GetDetailsAsync(string externalId, CancellationToken cancellationToken)
        {
            DetailsRequested.Add(externalId);
            return Task.FromResult(details(externalId));
        }
    }

    private sealed class FakeResolver(IBookMetadataProvider? provider) : IActiveMetadataProviderResolver
    {
        public Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IBookMetadataProvider>>(provider is null ? [] : [provider]);

        public Task<IBookMetadataProvider?> FindActiveProviderAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(provider is not null && provider.Id == providerId ? provider : null);
    }

    private static BookCandidate Candidate(string externalId, string title, string author, string? isbn = null, params BookSeriesCandidate[] series) =>
        new("fake", "Fake catalog", externalId, title, [author], null, null, null,
            isbn is null ? [] : [new BookEditionCandidate(title, isbn, "ebook", null)], series);

    private static ConversationBookTools Tools(CountingSearch search, RecordingAvailability availability, IBookMetadataProvider? provider = null) =>
        new(new User(), search, new FakeResolver(provider), null!, null!, availability, null!);

    // ---- series memory on offered books ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnOfferedBookRemembersThePrimarySeriesTheCatalogGaveIt()
    {
        var outcome = new CatalogSearchOutcome(
            [
                Candidate("a", "The Glass Orchard", "Ines Vale", null,
                    new BookSeriesCandidate("Orchard Chronicles (Reading Order)", "4", false), new BookSeriesCandidate("The Orchard Chronicles", "2", true)),
                Candidate("b", "A Book In Two Series, None Primary", "Ines Vale", null,
                    new BookSeriesCandidate("First Listed", "1", false), new BookSeriesCandidate("Second Listed", "9", false)),
                Candidate("c", "A Standalone", "Ines Vale")
            ],
            [], 1, false, []);
        var tools = Tools(new CountingSearch(outcome), new RecordingAvailability());

        var result = await tools.SearchBooksAsync("orchard", 5, CancellationToken.None);

        Assert.AreEqual("The Orchard Chronicles", result.Choices[0].SeriesName, "The series the catalog marks primary is the one remembered.");
        Assert.AreEqual("First Listed", result.Choices[1].SeriesName, "With none marked primary, the first listed is used.");
        Assert.IsNull(result.Choices[2].SeriesName, "A book in no series remembers none.");
    }

    // ---- library check by the offered record ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheLibraryCheckForAnOfferedBookReadsThatRecordAndNeverSearches()
    {
        var search = new CountingSearch(new CatalogSearchOutcome([], [], 1, false, []));
        var availability = new RecordingAvailability();
        var provider = new FakeProvider(id => id == "rec-7" ? Candidate("rec-7", "The Glass Orchard", "Ines Vale", "9781234567897") : null);
        var tools = Tools(search, availability, provider);

        var result = await tools.CheckLibraryForChoiceAsync(
            new ConversationChoiceRef("fake", "rec-7", "The Glass Orchard", "Ines Vale", null), CancellationToken.None);

        Assert.AreEqual("rec-7", provider.DetailsRequested.Single());
        Assert.AreEqual(0, search.Calls, "No text search: that is what picked the wrong record.");
        var identity = availability.Identities.Single();
        Assert.AreEqual("The Glass Orchard", identity.Title);
        Assert.AreEqual("Ines Vale", identity.Author);
        Assert.AreEqual("9781234567897", identity.Isbn13Candidates.Single(), "The record's own edition identifiers are used.");
        Assert.IsFalse(result!.Incomplete);
        Assert.AreEqual("The Glass Orchard", result.Title);
    }

    [TestMethod]
    public async Task ARecordWhoseDetailsCannotBeReadMakesTheLibraryCheckIncompleteNotAbsent()
    {
        var availability = new RecordingAvailability();
        var provider = new FakeProvider(_ => null);
        var tools = Tools(new CountingSearch(new CatalogSearchOutcome([], [], 1, false, [])), availability, provider);

        var result = await tools.CheckLibraryForChoiceAsync(
            new ConversationChoiceRef("fake", "gone", "The Glass Orchard", "Ines Vale", null), CancellationToken.None);

        Assert.IsTrue(result!.Incomplete, "Unreadable details must read as 'could not finish', never 'not in the library'.");
        Assert.IsEmpty(availability.Identities);
    }

    [TestMethod]
    public async Task AProviderThatIsNoLongerActiveYieldsNoCheck()
    {
        var tools = Tools(new CountingSearch(new CatalogSearchOutcome([], [], 1, false, [])), new RecordingAvailability(), provider: null);

        var result = await tools.CheckLibraryForChoiceAsync(
            new ConversationChoiceRef("fake", "rec-7", "The Glass Orchard", "Ines Vale", null), CancellationToken.None);

        Assert.IsNull(result);
    }
}
