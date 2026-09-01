using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class AudiobookshelfOwnedLibraryProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AnEbookRequestReturnsEmptyWithoutCallingAnything()
    {
        var context = ConfiguredContext();

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, context.ApiClient.CallCount);
    }

    [TestMethod]
    public async Task NotConfiguredReturnsEmpty()
    {
        var context = new TestContext();

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task DisabledReturnsEmpty()
    {
        var context = new TestContext();
        context.Settings.SetSettings("https://abs.example", "lib1", "folder1", null, Now);
        context.Settings.SetEnabled(false, null, Now);
        context.SettingsStore.Exists = true;

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task NoMatchingItemReturnsEmpty()
    {
        var context = ConfiguredContext();
        context.ApiClient.ExistingItemId = null;

        var options = await context.Provider.FindOwnedMatchesAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task AMatchReturnsOneOwnedOption()
    {
        var context = ConfiguredContext();
        context.ApiClient.ExistingItemId = "li_abc";
        var workId = Guid.NewGuid();

        var options = await context.Provider.FindOwnedMatchesAsync(
            workId, RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("audiobookshelf", option.ProviderId);
        Assert.AreEqual("li_abc", option.ProviderResultId);
        Assert.AreEqual(workId, option.WorkId);
        Assert.AreEqual(OptionKind.Owned, option.OptionKind);
        Assert.AreEqual(AcquisitionMethod.OwnedImport, option.AcquisitionMethod);
        Assert.IsNotNull(option.ExternalActionUri);
    }

    private static TestContext ConfiguredContext()
    {
        var context = new TestContext();
        context.Settings.SetSettings("https://abs.example", "lib1", "folder1", null, Now);
        context.Settings.SetEnabled(true, null, Now);
        context.SettingsStore.Exists = true;
        return context;
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            SettingsStore = new FakeAudiobookshelfSettingsStore(Settings);
            ApiClient = new FakeApiClient();
            WorkLookup = new FakeWorkLookup();

            Provider = new AudiobookshelfOwnedLibraryProvider(SettingsStore, ApiClient, WorkLookup);
        }

        public AudiobookshelfSettings Settings { get; } = new(Now);

        public FakeAudiobookshelfSettingsStore SettingsStore { get; }

        public FakeApiClient ApiClient { get; }

        public FakeWorkLookup WorkLookup { get; }

        public AudiobookshelfOwnedLibraryProvider Provider { get; }
    }

    private sealed class FakeAudiobookshelfSettingsStore(AudiobookshelfSettings settings)
        : IAudiobookshelfSettingsStore
    {
        public bool Exists { get; set; }

        public Task<AudiobookshelfSettings?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Exists ? settings : null);

        public Task<AudiobookshelfSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeApiClient : IAudiobookshelfApiClient
    {
        public string? ExistingItemId { get; set; }

        public int CallCount { get; private set; }

        public Task<string?> FindExistingItemIdAsync(
            string title, string? author, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(ExistingItemId);
        }

        public Task<AudiobookshelfUploadResult> UploadAsync(
            Stream content, string filename, string title, string? author, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AudiobookshelfUploadResult> UploadBundleAsync(
            IReadOnlyList<(Stream Content, string Filename)> tracks,
            string title,
            string? author,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Hobbit", "J. R. R. Tolkien", []));
    }
}
