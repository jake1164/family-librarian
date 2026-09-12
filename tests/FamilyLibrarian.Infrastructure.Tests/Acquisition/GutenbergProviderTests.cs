using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
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
