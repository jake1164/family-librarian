using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Acquisition;
using FamilyLibrarian.Infrastructure.Gutenberg;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class GutenbergProviderTests
{
    [TestMethod]
    public async Task NotRegisteredReturnsEmpty()
    {
        var context = new TestContext();
        context.Registry.Descriptor = null;

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task DisabledReturnsEmpty()
    {
        var context = new TestContext();
        context.Registry.Descriptor = UsableDescriptor with { DefaultEnabled = false };

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task BlankTitleReturnsEmptyWithoutCallingTheCatalog()
    {
        var context = new TestContext();

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("  ", null, []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, context.Catalog.SearchCallCount);
    }

    [TestMethod]
    public async Task NoCatalogMatchReturnsEmpty()
    {
        var context = new TestContext();
        context.Catalog.Books = [];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task AMatchingEbookReturnsOneDirectAcquisitionOptionWithNoWorkId()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                2701,
                "Moby Dick",
                "moby dick",
                "Ebook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["en"],
                [new GutenbergCatalogFormat("2701/2701-images.epub", "application/epub+zip", GutenbergFormatKind.EpubImages, 500_000, null)])
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("gutendex", option.ProviderId);
        Assert.AreEqual("2701", option.ProviderResultId);
        Assert.AreEqual(Guid.Empty, option.WorkId);
        Assert.AreEqual(OptionKind.DirectAcquisition, option.OptionKind);
        Assert.AreEqual("Public domain", option.LicenseOrUsageStatus);
    }

    [TestMethod]
    public async Task AnAudiobookCandidateIncludesTheSourceRecordAndMediaFactsNeededForReview()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                9147,
                "Moby Dick",
                "moby dick",
                "Audiobook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["en"],
                [
                    new GutenbergCatalogFormat("9147/track-01.mp3", "audio/mpeg", GutenbergFormatKind.AudioMp3, 1_000_000, null),
                    new GutenbergCatalogFormat("9147/track-02.mp3", "audio/mpeg", GutenbergFormatKind.AudioMp3, 2_000_000, null)
                ],
                DownloadCount: 5_505)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options.Single();
        Assert.AreEqual("mp3", option.Format);
        Assert.AreEqual(2, option.PartCount);
        Assert.AreEqual(3_000_000L, option.SizeBytes);
        Assert.AreEqual(5_505, option.ProviderPopularity);
        StringAssert.Contains(
            RequestReviewCandidatePresentation.BuildDetails(option) ?? string.Empty,
            "5,505 source downloads");
        Assert.AreEqual("https://www.gutenberg.org/ebooks/9147", option.AdminInspectionUri?.ToString());
    }

    [TestMethod]
    public async Task ARecordPublishingSeveralAudioCodecsAutoSelectsTheHighestRankedOne()
    {
        // Reproduces real Gutenberg data found live at #28794: the same
        // record publishes MP3, M4B, and Ogg Vorbis side by side. M4B must
        // win per AudiobookFormatPolicy even though its tracks were parsed
        // last and are individually smaller than the MP3 ones.
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                28794,
                "Moby Dick",
                "moby dick",
                "Audiobook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["en"],
                [
                    new GutenbergCatalogFormat("28794/mp3/28794-01.mp3", "audio/mpeg", GutenbergFormatKind.AudioMp3, 9_000_000, null),
                    new GutenbergCatalogFormat("28794/ogg/28794-01.ogg", "audio/ogg", GutenbergFormatKind.AudioOgg, 6_000_000, null),
                    new GutenbergCatalogFormat("28794/m4b/28794-01.m4b", "audio/mp4", GutenbergFormatKind.AudioM4b, 3_000_000, null)
                ],
                DownloadCount: 5_505)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options.Single();
        Assert.AreEqual("m4b", option.Format);
        Assert.AreEqual(3_000_000L, option.SizeBytes);
        StringAssert.Contains(
            RequestReviewCandidatePresentation.BuildDetails(option) ?? string.Empty,
            "M4B AUDIOBOOK");
    }

    [TestMethod]
    public async Task ARecordWithOnlyUnsupportedAudioCodecsReturnsNoAudiobookOption()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                28794,
                "Moby Dick",
                "moby dick",
                "Audiobook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["en"],
                [new GutenbergCatalogFormat("28794/spx/28794-01.spx", "audio/ogg", GutenbergFormatKind.Other, 1_000_000, null)],
                DownloadCount: 5_505)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task TheGuidBasedPathStampsTheRealWorkId()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                2701,
                "Moby Dick",
                "moby dick",
                "Ebook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["en"],
                [new GutenbergCatalogFormat("2701/2701-images.epub", "application/epub+zip", GutenbergFormatKind.EpubImages, 500_000, null)])
        ];
        var workId = Guid.NewGuid();

        var options = await context.Provider.FindDirectAcquisitionsAsync(workId, RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual(workId, options[0].WorkId);
    }

    [TestMethod]
    public async Task AForeignOnlyMatchIsReturnedMarkedAsRequiringLanguageConfirmation()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                2701,
                "Moby Dick",
                "moby dick",
                "Ebook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["es"],
                [new GutenbergCatalogFormat("2701/2701-images.epub", "application/epub+zip", GutenbergFormatKind.EpubImages, 500_000, null)])
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.IsTrue(options[0].RequiresLanguageConfirmation);
        Assert.AreEqual("es", options[0].Language);
    }

    [TestMethod]
    public async Task AnEnglishMatchIsPreferredOverAnEarlierForeignCandidate()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            new GutenbergCatalogBook(
                1,
                "Moby Dick",
                "moby dick",
                "Ebook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["es"],
                [new GutenbergCatalogFormat("1/1-images.epub", "application/epub+zip", GutenbergFormatKind.EpubImages, 500_000, null)]),
            new GutenbergCatalogBook(
                2701,
                "Moby Dick",
                "moby dick",
                "Ebook",
                "Public domain",
                [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
                ["en"],
                [new GutenbergCatalogFormat("2701/2701-images.epub", "application/epub+zip", GutenbergFormatKind.EpubImages, 500_000, null)])
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("2701", options[0].ProviderResultId);
        Assert.IsFalse(options[0].RequiresLanguageConfirmation);
    }

    [TestMethod]
    public async Task AClearlyDominantDownloadCountAutoResolvesInsteadOfStayingAmbiguous()
    {
        // Reproduces real Gutenberg data found live: three English "Moby Dick"
        // entries where #2701 has ~6.5x the next-highest download count.
        var context = new TestContext();
        context.Catalog.Books =
        [
            EnglishMobyDickCandidate(15, downloadCount: 3_856),
            EnglishMobyDickCandidate(2489, downloadCount: 25_142),
            EnglishMobyDickCandidate(2701, downloadCount: 164_301)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("2701", options[0].ProviderResultId);
        StringAssert.Contains(options[0].AutomaticSelectionReason, "164,301 downloads");
        StringAssert.Contains(options[0].AutomaticSelectionReason, "6.5× the runner-up record #2489");
    }

    [TestMethod]
    public async Task CloseDownloadCountsStayAmbiguousRatherThanGuessing()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            EnglishMobyDickCandidate(2489, downloadCount: 25_142),
            EnglishMobyDickCandidate(2701, downloadCount: 40_000)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(2, options.Count);
    }

    [TestMethod]
    public async Task MissingDownloadCountsStayAmbiguousRatherThanGuessing()
    {
        var context = new TestContext();
        context.Catalog.Books =
        [
            EnglishMobyDickCandidate(15, downloadCount: null),
            EnglishMobyDickCandidate(2701, downloadCount: null)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(2, options.Count);
    }

    [TestMethod]
    public async Task ADominantDownloadCountBelowTheMinimumFloorStaysAmbiguous()
    {
        // 900 vs 10 is a huge ratio but neither number is a meaningful signal
        // at that scale -- the absolute floor exists so two barely-downloaded
        // entries don't "dominate" each other on noise.
        var context = new TestContext();
        context.Catalog.Books =
        [
            EnglishMobyDickCandidate(15, downloadCount: 10),
            EnglishMobyDickCandidate(2701, downloadCount: 900)
        ];

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(2, options.Count);
    }

    private static GutenbergCatalogBook EnglishMobyDickCandidate(int gutenbergId, int? downloadCount) => new(
        gutenbergId,
        "Moby Dick",
        "moby dick",
        "Ebook",
        "Public domain",
        [new GutenbergCatalogPerson("Herman Melville", GutenbergPersonRole.Author)],
        ["en"],
        [new GutenbergCatalogFormat(
            $"{gutenbergId}/{gutenbergId}-images.epub", "application/epub+zip", GutenbergFormatKind.EpubImages, 500_000, null)],
        downloadCount);

    private static readonly ProviderDescriptor UsableDescriptor = new(
        "gutendex",
        "Project Gutenberg",
        new HashSet<ProviderCapability>(),
        RequiresCredential: false,
        HasExternallyManagedCredential: false,
        DefaultEnabled: true);

    private sealed class TestContext
    {
        public TestContext()
        {
            Catalog = new FakeGutenbergCatalog();
            Registry = new FakeProviderRegistry { Descriptor = UsableDescriptor };
            WorkLookup = new FakeWorkLookup();

            Provider = new GutenbergProvider(
                Catalog,
                Registry,
                new FakeProviderSettingsStore(),
                WorkLookup,
                new FakeFileResolver(),
                new HttpClient(),
                new ManualImportPolicy(),
                new DeterministicBookMatcher());
        }

        public FakeGutenbergCatalog Catalog { get; }

        public FakeProviderRegistry Registry { get; }

        public FakeWorkLookup WorkLookup { get; }

        public GutenbergProvider Provider { get; }
    }

    private sealed class FakeGutenbergCatalog : IGutenbergCatalog
    {
        public IReadOnlyList<GutenbergCatalogBook> Books { get; set; } = [];

        public int SearchCallCount { get; private set; }

        public Task<GutenbergCatalogStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GutenbergCatalogStatus(true, null, null, null, 0, 0, 0, 0, null, "Ready", null));

        public Task<IReadOnlyList<GutenbergCatalogBook>> SearchAsync(
            GutenbergCatalogSearchQuery query, CancellationToken cancellationToken)
        {
            SearchCallCount++;
            return Task.FromResult(Books);
        }
    }

    private sealed class FakeProviderRegistry : IProviderRegistry
    {
        public ProviderDescriptor? Descriptor { get; set; }

        public IReadOnlyList<ProviderDescriptor> GetInstalledProviders() => Descriptor is null ? [] : [Descriptor];

        public ProviderDescriptor? Find(string providerId) =>
            Descriptor?.Id == providerId ? Descriptor : null;
    }

    private sealed class FakeProviderSettingsStore : IProviderSettingsStore
    {
        public Task<IReadOnlyList<ProviderSetting>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderSetting>>([]);

        public Task<ProviderSetting?> FindAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<ProviderSetting?>(null);

        public Task<ProviderSetting> GetOrCreateAsync(string providerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "Moby Dick", "Herman Melville", []));
    }

    private sealed class FakeFileResolver : IGutenbergFileResolver
    {
        public IReadOnlyList<Uri> Resolve(string sourcePath, GutenbergFormatKind formatKind) => [];
    }
}
