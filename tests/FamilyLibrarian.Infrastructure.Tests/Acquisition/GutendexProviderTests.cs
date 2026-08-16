using System.Net;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class GutendexProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private const string MatchingResponse = """
        {
          "count": 1,
          "next": null,
          "previous": null,
          "results": [
            {
              "id": 1234,
              "title": "The Hobbit",
              "authors": [{"name": "Tolkien, J. R. R.", "birth_year": 1892, "death_year": 1973}],
              "formats": {
                "application/epub+zip": "https://www.gutenberg.org/ebooks/1234.epub.noimages",
                "text/plain": "https://www.gutenberg.org/files/1234/1234-0.txt"
              }
            }
          ]
        }
        """;

    private const string NoMatchResponse = """{"count": 0, "next": null, "previous": null, "results": []}""";

    [TestMethod]
    public async Task AnAudiobookRequestReturnsEmptyWithoutCallingTheApi()
    {
        var handler = new RecordingHandler(MatchingResponse);
        var context = new TestContext(handler, enabled: true);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task ADisabledProviderReturnsEmptyWithoutCallingTheApi()
    {
        var handler = new RecordingHandler(MatchingResponse);
        var context = new TestContext(handler, enabled: false);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task NoMatchingResultReturnsEmpty()
    {
        var handler = new RecordingHandler(NoMatchResponse);
        var context = new TestContext(handler, enabled: true);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(1, handler.CallCount);
    }

    [TestMethod]
    public async Task AMatchReturnsOneDirectAcquisitionOptionWithTheEpubUrl()
    {
        var handler = new RecordingHandler(MatchingResponse);
        var context = new TestContext(handler, enabled: true);
        var workId = Guid.NewGuid();

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            workId, RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("gutendex", option.ProviderId);
        Assert.AreEqual("1234", option.ProviderResultId);
        Assert.AreEqual(workId, option.WorkId);
        Assert.AreEqual(OptionKind.DirectAcquisition, option.OptionKind);
        Assert.AreEqual(AcquisitionMethod.DirectDownload, option.AcquisitionMethod);
        Assert.AreEqual("https://www.gutenberg.org/ebooks/1234.epub.noimages", option.ProviderData);
    }

    [TestMethod]
    public async Task FetchAsyncDownloadsFromTheStoredProviderDataUrl()
    {
        var handler = new RecordingHandler("epub file bytes");
        var context = new TestContext(handler, enabled: true);
        var option = new FulfillmentOption(
            ProviderId: "gutendex",
            ProviderResultId: "1234",
            WorkId: Guid.NewGuid(),
            EditionId: null,
            MediaType: RequestMediaType.Ebook,
            OptionKind: OptionKind.DirectAcquisition,
            AcquisitionMethod: AcquisitionMethod.DirectDownload,
            Format: "epub",
            Language: null,
            Quality: null,
            Availability: null,
            Cost: 0m,
            Currency: null,
            LicenseOrUsageStatus: "Public domain",
            DrmStatus: null,
            ExternalActionUri: null,
            ProviderData: "https://www.gutenberg.org/ebooks/1234.epub.noimages");

        var file = await context.Provider.FetchAsync(option, CancellationToken.None);

        Assert.AreEqual("gutenberg-1234.epub", file.Filename);
        using var reader = new StreamReader(file.Content);
        Assert.AreEqual("epub file bytes", await reader.ReadToEndAsync());
        Assert.AreEqual(
            "https://www.gutenberg.org/ebooks/1234.epub.noimages", handler.LastRequestUri?.ToString());
    }

    private sealed class TestContext
    {
        public TestContext(RecordingHandler handler, bool enabled)
        {
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://gutendex.com/") };
            var descriptor = new ProviderDescriptor(
                "gutendex",
                "Gutendex (Project Gutenberg)",
                new HashSet<ProviderCapability> { ProviderCapability.DirectAcquisition },
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                DefaultEnabled: false);
            var setting = enabled ? new ProviderSetting("gutendex", Now) : null;
            setting?.SetEnabled(true, null, Now);

            Provider = new GutendexProvider(
                httpClient,
                new FakeProviderRegistry(descriptor),
                new FakeProviderSettingsStore(setting),
                new FakeWorkLookup());
        }

        public GutendexProvider Provider { get; }
    }

    private sealed class FakeProviderRegistry(ProviderDescriptor descriptor) : IProviderRegistry
    {
        public IReadOnlyList<ProviderDescriptor> GetInstalledProviders() => [descriptor];

        public ProviderDescriptor? Find(string providerId) => descriptor;
    }

    private sealed class FakeProviderSettingsStore(ProviderSetting? setting) : IProviderSettingsStore
    {
        public Task<IReadOnlyList<ProviderSetting>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderSetting>>(setting is null ? [] : [setting]);

        public Task<ProviderSetting?> FindAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult(setting);

        public Task<ProviderSetting> GetOrCreateAsync(string providerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, "The Hobbit", "J. R. R. Tolkien"));
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8)
            };
            return Task.FromResult(response);
        }
    }
}
