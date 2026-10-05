using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class ExternalCandidateAvailabilityCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AuthorlessExactTitlesParticipateInDeterministicAutomaticSelection()
    {
        var context = new TestContext();
        var provider = NewProvider("example-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates = [Candidate("b", "epub", ExternalProviderDrmStatus.None), Candidate("a", "epub", ExternalProviderDrmStatus.None)];
        context.Client.Candidates = context.Client.Candidates.Select(candidate => candidate with
            { Work = candidate.Work with { Authors = [] } }).ToArray();
        var options = await context.Checker.FindAsync(new BookIdentity("Moby Dick", "Herman Melville", []),
            RequestMediaType.Ebook, CancellationToken.None);
        Assert.AreEqual(1, options.Count(option => option.MatchBasis == BookMatchBasis.StrictTitle));
        Assert.AreEqual("a", options.Single(option => option.MatchBasis == BookMatchBasis.StrictTitle).ProviderResultId);
        Assert.IsFalse(options.Any(option => option.RequiresReleaseConfirmation));
    }

    [TestMethod]
    public async Task ATitleConfirmedFragmentRemainsIncompleteEvenWhenTheSourceReportsOneFile()
    {
        var context = new TestContext();
        var provider = NewProvider("example-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates = [new ExternalProviderCandidate("part-two", ExternalProviderWorkEvidence.Empty,
            Release: new ExternalProviderReleaseEvidence("Moby.Dick.2.of.2", "m4b", 500_000,
                false, 1, false, null, null, [], null))];
        var option = (await context.Checker.FindAsync(new BookIdentity("Moby Dick", "Herman Melville", []),
            RequestMediaType.Audiobook, CancellationToken.None)).Single();
        Assert.AreEqual(BookMatchBasis.StrictTitle, option.MatchBasis);
        Assert.IsTrue(option.HasPlausibleTitle);
        Assert.IsTrue(option.RequiresReleaseConfirmation);
        Assert.AreEqual(2, option.AudiobookPart!.Number);
        StringAssert.Contains(option.ReleaseConcern!, "Part 2 of 2");
        StringAssert.Contains(FamilyLibrarian.Application.Requests.RequestReviewCandidatePresentation.BuildDetails(option)!, "Part 2 of 2");
    }

    [TestMethod]
    public async Task ACompleteAuthorlessCopyIsNotDemotedByAStrongAuthorFragment()
    {
        var context = new TestContext();
        var provider = NewProvider("example-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        var complete = Candidate("complete", "m4b", ExternalProviderDrmStatus.None);
        var fragment = Candidate("fragment", "m4b", ExternalProviderDrmStatus.None);
        context.Client.Candidates = [complete with { Work = complete.Work with { Authors = [] } },
            fragment with { Release = fragment.Release! with { Name = "Moby Dick Part 2 of 2" } }];
        var options = await context.Checker.FindAsync(new BookIdentity("Moby Dick", "Herman Melville", []),
            RequestMediaType.Audiobook, CancellationToken.None);
        Assert.AreEqual(BookMatchBasis.StrictTitle, options.Single(option => option.ProviderResultId == "complete").MatchBasis);
        Assert.IsTrue(options.Single(option => option.ProviderResultId == "fragment").RequiresReleaseConfirmation);
    }

    [TestMethod]
    public async Task NoEnabledProvidersReturnsEmptyWithoutCallingTheClient()
    {
        var context = new TestContext();

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, context.Client.CallCount);
    }

    [TestMethod]
    public async Task AProviderSearchFailureDegradesToEmptyRatherThanThrowing()
    {
        var context = new TestContext();
        var provider = NewProvider("flaky-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Throw = true;

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task AMatchingProviderReturnsADirectAcquisitionOptionWithNoWorkId()
    {
        var context = new TestContext();
        var provider = NewProvider("free-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates = [Candidate("ref-1", "epub", ExternalProviderDrmStatus.None)];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("free-source", option.ProviderId);
        Assert.AreEqual("ref-1", option.ProviderResultId);
        Assert.AreEqual(Guid.Empty, option.WorkId);
        Assert.AreEqual(OptionKind.DirectAcquisition, option.OptionKind);
        Assert.IsFalse(option.RequiresReleaseConfirmation);
        CollectionAssert.AreEquivalent(
            ExternalEbookFormatPolicy.SearchFormats.ToArray(),
            context.Client.LastSearchRequest!.Constraints!.Formats!.ToArray());
    }

    [TestMethod]
    public async Task TheProviderIsSearchedWithoutTheSubtitle()
    {
        var context = new TestContext();
        var provider = NewProvider("free-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);

        await context.Checker.FindAsync(
            new BookIdentity(
                "Threshing Day: Return to the Empyrean world with thirteen stories", "Rebecca Yarros", []),
            RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual("Threshing Day", context.Client.LastSearchRequest!.Work.Title);
    }

    [TestMethod]
    public async Task ACollectionCandidateIsFlaggedForReleaseConfirmation()
    {
        var context = new TestContext();
        var provider = NewProvider("collection-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates =
        [
            new ExternalProviderCandidate(
                "ref-1",
                new ExternalProviderWorkEvidence("Moby Dick", null, [new BookAuthor("Herman Melville", "author")], [], []),
                Release: new ExternalProviderReleaseEvidence(
                    "Melville-Omnibus", "epub", null, IsCollection: true, PartCount: 3,
                    IsSample: false, IsAbridged: null, IsUnabridged: null, QualityTags: [], AgeDays: null))
        ];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        var option = options.Single();
        Assert.IsTrue(option.RequiresReleaseConfirmation);
        Assert.Contains("collection", option.ReleaseConcern!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task RejectTierAndEncryptedCandidatesAreRemovedBeforeTheyCanBeAcquired()
    {
        var context = new TestContext();
        var provider = NewProvider("format-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates =
        [
            Candidate("plain-text", "txt", ExternalProviderDrmStatus.None),
            Candidate("encrypted-azw3", "azw3", ExternalProviderDrmStatus.Encrypted)
        ];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task EnabledProvidersAreSearchedConcurrently()
    {
        var context = new TestContext();
        var first = NewProvider("first-source");
        var second = NewProvider("second-source");
        first.SetEnabled(true, null, Now);
        second.SetEnabled(true, null, Now);
        context.Store.Providers.Add(first);
        context.Store.Providers.Add(second);
        context.Client.RequiredConcurrentCalls = 2;
        context.Client.BlockSearches = true;

        var lookup = context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        await context.Client.RequiredCallsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        context.Client.ReleaseSearches.TrySetResult();
        await lookup;
    }

    [TestMethod]
    public async Task ADetectedSafeSourceSuppressesPossibleConversionSources()
    {
        var context = new TestContext();
        var provider = NewProvider("format-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates =
        [
            Candidate("safe-azw3", "azw3", ExternalProviderDrmStatus.None),
            Candidate("possible-docx", "docx", ExternalProviderDrmStatus.None)
        ];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("safe-azw3", options.Single().ProviderResultId);
        Assert.IsFalse(options.Single().RequiresReleaseConfirmation);
    }

    [TestMethod]
    public async Task UnknownDrmCanBeReviewedButNeverAutomaticallyAcquired()
    {
        var context = new TestContext();
        var provider = NewProvider("format-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates = [Candidate("unknown-drm", "epub", ExternalProviderDrmStatus.Unknown)];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.IsTrue(options.Single().RequiresReleaseConfirmation);
    }

    [TestMethod]
    public async Task StrictEquivalentDuplicatesSelectOneEnglishEpubInsteadOfAskingForAnArbitraryChoice()
    {
        var context = new TestContext();
        var provider = NewProvider("format-source");
        provider.SetEnabled(true, null, Now);
        context.Store.Providers.Add(provider);
        context.Client.Candidates =
        [
            Candidate("french-epub", "epub", ExternalProviderDrmStatus.None, language: "fr"),
            Candidate("english-mobi", "mobi", ExternalProviderDrmStatus.None, language: "en"),
            Candidate("english-epub", "epub", ExternalProviderDrmStatus.None, language: "en")
        ];

        var options = await context.Checker.FindAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.HasCount(2, options);
        Assert.AreEqual("english-epub", options.Single(option => option.MatchBasis == BookMatchBasis.StrictTitleAuthor).ProviderResultId);
        Assert.IsFalse(options.Any(option => option.ProviderResultId == "french-epub"));
    }

    private static readonly BookIdentity Fahrenheit = new("Fahrenheit 451", "Ray Bradbury", []);

    private static TestContext CoalescingContext(params string[] providerIds)
    {
        var context = new TestContext(coalesce: true);
        foreach (var providerId in providerIds)
        {
            var provider = NewProvider(providerId);
            provider.SetEnabled(true, null, Now);
            context.Store.Providers.Add(provider);
        }

        context.Client.Candidates = [Candidate("ref-1", "epub", ExternalProviderDrmStatus.None)];
        return context;
    }

    [TestMethod]
    public async Task ConcurrentIdenticalSearchesShareOneProviderCall()
    {
        var context = CoalescingContext("free-source");
        context.Client.BlockSearches = true;
        context.Client.RequiredConcurrentCalls = 1;

        var lookups = Enumerable.Range(0, 6)
            .Select(_ => context.Checker.FindAsync(
                new BookIdentity("Fahrenheit 451", "Ray Bradbury", []), RequestMediaType.Ebook, CancellationToken.None))
            .ToArray();
        await context.Client.RequiredCallsStarted.Task;
        context.Client.ReleaseSearches.SetResult();
        var results = await Task.WhenAll(lookups);

        Assert.AreEqual(1, context.Client.CallCount);
        Assert.IsTrue(results.All(options => options.Single().ProviderResultId == "ref-1"));
    }

    [TestMethod]
    public async Task ARepeatedSearchReusesTheRecentResultButMediaTypeIsKeptSeparate()
    {
        var context = CoalescingContext("free-source");

        await context.Checker.FindAsync(Fahrenheit, RequestMediaType.Ebook, CancellationToken.None);
        await context.Checker.FindAsync(
            new BookIdentity("  fahrenheit   451 ", "RAY BRADBURY", []), RequestMediaType.Ebook, CancellationToken.None);
        Assert.AreEqual(1, context.Client.CallCount);

        await context.Checker.FindAsync(Fahrenheit, RequestMediaType.Audiobook, CancellationToken.None);
        await context.Checker.FindAsync(
            new BookIdentity("Fahrenheit 451", "Ray Bradbury", ["9781451673319"]), RequestMediaType.Ebook, CancellationToken.None);
        Assert.AreEqual(3, context.Client.CallCount);
    }

    [TestMethod]
    public async Task AFailedSearchIsNotCachedSoTheNextCallerAsksAgain()
    {
        var context = CoalescingContext("flaky-source");
        context.Client.Throw = true;

        var failed = await context.Checker.FindAsync(Fahrenheit, RequestMediaType.Ebook, CancellationToken.None);
        context.Client.Throw = false;
        var recovered = await context.Checker.FindAsync(Fahrenheit, RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, failed.Count);
        Assert.AreEqual(1, recovered.Count);
        Assert.AreEqual(2, context.Client.CallCount);
    }

    [TestMethod]
    public async Task AcquisitionLookupsAlwaysSeeAFreshProviderResponse()
    {
        var context = CoalescingContext("free-source");
        var provider = context.Store.Providers.Single();

        await context.Checker.FindForProviderAsync(provider, Fahrenheit, RequestMediaType.Ebook, CancellationToken.None);
        await context.Checker.FindForProviderAsync(provider, Fahrenheit, RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(2, context.Client.CallCount);
    }

    [TestMethod]
    public async Task OneCallerLeavingDoesNotCancelTheSearchAnotherCallerIsStillWaitingOn()
    {
        var context = CoalescingContext("free-source");
        context.Client.BlockSearches = true;
        context.Client.RequiredConcurrentCalls = 1;
        using var leaving = new CancellationTokenSource();

        var leaver = context.Checker.FindAsync(Fahrenheit, RequestMediaType.Ebook, leaving.Token);
        var stayer = context.Checker.FindAsync(Fahrenheit, RequestMediaType.Ebook, CancellationToken.None);
        await context.Client.RequiredCallsStarted.Task;
        await leaving.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => leaver);
        context.Client.ReleaseSearches.SetResult();

        Assert.AreEqual("ref-1", (await stayer).Single().ProviderResultId);
        Assert.AreEqual(1, context.Client.CallCount);
    }

    [TestMethod]
    public async Task TheProviderCallIsCancelledOnceEveryCallerHasLeft()
    {
        var context = CoalescingContext("free-source");
        context.Client.BlockSearches = true;
        context.Client.RequiredConcurrentCalls = 1;
        using var only = new CancellationTokenSource();

        var lookup = context.Checker.FindAsync(Fahrenheit, RequestMediaType.Ebook, only.Token);
        await context.Client.RequiredCallsStarted.Task;
        await only.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup);

        await context.Client.SearchCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static ExternalProviderCandidate Candidate(
        string providerReference, string sourceFormat, ExternalProviderDrmStatus drmStatus, string? language = null) =>
        new(
            providerReference,
            new ExternalProviderWorkEvidence(
                "Moby Dick", null, [new BookAuthor("Herman Melville", "author")], [], []),
            Edition: new ExternalProviderEditionEvidence(language, null, null, []),
            Release: new ExternalProviderReleaseEvidence(
                $"Moby-Dick.{sourceFormat}", sourceFormat, 500_000, false, 1, false, null, null, [], null,
                drmStatus));

    private static ExternalProvider NewProvider(string providerId) =>
        new(providerId, providerId, "https://example.test", Now);

    private sealed class TestContext
    {
        public TestContext(bool coalesce = false)
        {
            Store = new FakeExternalProviderStore();
            Client = new FakeExternalProviderClient();
            Checker = new ExternalCandidateAvailabilityChecker(
                Store,
                Client,
                                new ExternalProviderMatchVerifier(
                    new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()),
                    new DeterministicBookMatcher()),
                new NoOpCredentialProtector(),
                coalesce ? new ExternalSearchCoalescer(TimeProvider.System) : null);
        }

        public FakeExternalProviderStore Store { get; }

        public FakeExternalProviderClient Client { get; }

        public ExternalCandidateAvailabilityChecker Checker { get; }
    }

    private sealed class FakeExternalProviderStore : IExternalProviderStore
    {
        public List<ExternalProvider> Providers { get; } = [];

        public Task<IReadOnlyList<ExternalProvider>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>(Providers);

        public Task<IReadOnlyList<ExternalProvider>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalProvider>>(Providers.Where(provider => provider.IsEnabled).ToArray());

        public Task<ExternalProvider?> FindAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Providers.FirstOrDefault(provider => provider.Id == id));

        public Task<ExternalProvider?> FindByProviderIdAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(Providers.FirstOrDefault(provider => provider.ProviderId == providerId));

        public void Add(ExternalProvider provider) => Providers.Add(provider);

        public void Remove(ExternalProvider provider) => Providers.Remove(provider);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeExternalProviderClient : IExternalProviderClient
    {
        public IReadOnlyList<ExternalProviderCandidate> Candidates { get; set; } = [];

        public bool Throw { get; set; }

        private int callCount;

        public int CallCount => callCount;

        public int RequiredConcurrentCalls { get; set; }

        public bool BlockSearches { get; set; }

        public TaskCompletionSource RequiredCallsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSearches { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SearchCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExternalProviderSearchRequest? LastSearchRequest { get; private set; }

        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderHealth> GetHealthAsync(
            string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string providerReference, RequestMediaType mediaType,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
            string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> GetOutputAsync(
            string baseUrl, string? apiKey, string jobId, string outputId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAcquireAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAcquireAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request,
            CancellationToken cancellationToken)
        {
            var calls = Interlocked.Increment(ref callCount);
            LastSearchRequest = request;
            if (RequiredConcurrentCalls > 0 && calls >= RequiredConcurrentCalls)
            {
                RequiredCallsStarted.TrySetResult();
            }

            if (BlockSearches)
            {
                try
                {
                    await ReleaseSearches.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SearchCancelled.TrySetResult();
                    throw;
                }
            }

            if (Throw)
            {
                throw new HttpRequestException("The external provider is unavailable.");
            }

            return Candidates;
        }
    }

    private sealed class NoOpCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;

        public string Protect(string providerId, string plaintext) => plaintext;

        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }
}
