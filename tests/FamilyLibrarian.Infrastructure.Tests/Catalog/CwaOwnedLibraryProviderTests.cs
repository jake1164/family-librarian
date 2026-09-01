using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class CwaOwnedLibraryProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnAudiobookRequestReturnsEmptyWithoutCallingAnything()
    {
        var context = ConfiguredContext();

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, context.CatalogClient.CallCount);
    }

    [TestMethod]
    public async Task NotConfiguredReturnsEmpty()
    {
        var context = new TestContext();

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task DisabledReturnsEmpty()
    {
        var context = new TestContext();
        context.Settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example", null, null, Now);
        context.Settings.SetEnabled(false, null, Now);
        context.SettingsStore.Exists = true;

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task NoCatalogMatchReturnsEmpty()
    {
        var context = ConfiguredContext();
        context.CatalogClient.NextBookId = null;

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task AMatchReturnsOneOwnedOption()
    {
        var context = ConfiguredContext();
        context.CatalogClient.NextBookId = "42";
        var workId = Guid.NewGuid();

        var options = await context.Provider.FindOwnedMatchesAsync(
            workId, RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("cwa", option.ProviderId);
        Assert.AreEqual("42", option.ProviderResultId);
        Assert.AreEqual(workId, option.WorkId);
        Assert.AreEqual(OptionKind.Owned, option.OptionKind);
        Assert.AreEqual(AcquisitionMethod.OwnedImport, option.AcquisitionMethod);
        Assert.IsNotNull(option.ExternalActionUri);
    }

    [TestMethod]
    public async Task PassesTheWorksKnownIsbnsToTheCatalogClient()
    {
        var context = ConfiguredContext();
        var isbns = new[] { "9780000000001" };
        context.WorkLookup.Isbn13s = isbns;
        context.CatalogClient.NextBookId = "42";

        await context.Provider.FindOwnedMatchesAsync(Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        CollectionAssert.AreEquivalent(isbns, context.CatalogClient.LastIsbn13Candidates!.ToArray());
    }

    private static TestContext ConfiguredContext()
    {
        var context = new TestContext();
        context.Settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example", null, null, Now);
        context.Settings.SetEnabled(true, null, Now);
        context.SettingsStore.Exists = true;
        return context;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            SettingsStore = new FakeCwaSettingsStore(Settings);
            CatalogClient = new FakeCatalogClient();
            WorkLookup = new FakeWorkLookup();

            Provider = new CwaOwnedLibraryProvider(SettingsStore, CatalogClient, WorkLookup);
        }

        public CwaSettings Settings { get; } = new(Now);

        public FakeCwaSettingsStore SettingsStore { get; }

        public FakeCatalogClient CatalogClient { get; }

        public FakeWorkLookup WorkLookup { get; }

        public CwaOwnedLibraryProvider Provider { get; }
    }

    private sealed class FakeCwaSettingsStore(CwaSettings settings) : ICwaSettingsStore
    {
        public bool Exists { get; set; }

        public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Exists ? settings : null);

        public Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCatalogClient : ICwaCatalogClient
    {
        public string? NextBookId { get; set; }

        public int CallCount { get; private set; }

        public IReadOnlyCollection<string>? LastIsbn13Candidates { get; private set; }

        public Task<string?> FindBookIdAsync(
            string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken)
        {
            CallCount++;
            LastIsbn13Candidates = isbn13Candidates;
            return Task.FromResult(NextBookId);
        }
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public IReadOnlyList<string> Isbn13s { get; set; } = [];

        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Hobbit", "J. R. R. Tolkien", Isbn13s));
    }
}
