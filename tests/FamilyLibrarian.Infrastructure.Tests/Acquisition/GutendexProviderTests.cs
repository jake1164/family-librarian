using System.Net;
using System.Text;
using FamilyLibrarian.Application.Acquisition;
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

    private const string MatchingSoundResponse = """
        {
          "count": 1,
          "next": null,
          "previous": null,
          "results": [
            {
              "id": 22979,
              "title": "The Hobbit",
              "media_type": "Sound",
              "authors": [{"name": "Tolkien, J. R. R.", "birth_year": 1892, "death_year": 1973}],
              "formats": {}
            }
          ]
        }
        """;

    [TestMethod]
    public async Task AnAudiobookMatchReturnsOneBundleOptionWithEveryRdfTrack()
    {
        var tracks = new[]
        {
            new GutenbergAudioTrack(new Uri("https://www.gutenberg.org/files/22979/mp3/22979-01.mp3"), ".mp3"),
            new GutenbergAudioTrack(new Uri("https://www.gutenberg.org/files/22979/mp3/22979-02.mp3"), ".mp3")
        };
        var catalog = new FakeGutenbergAudiobookCatalog { TracksById = { [22979] = tracks } };
        var context = new TestContext(new RecordingHandler(MatchingSoundResponse), enabled: true, catalog: catalog);
        var workId = Guid.NewGuid();

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            workId, RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        var option = options[0];
        Assert.AreEqual("gutendex", option.ProviderId);
        Assert.AreEqual("22979", option.ProviderResultId);
        Assert.AreEqual(RequestMediaType.Audiobook, option.MediaType);
        Assert.AreEqual(1, catalog.CallCount);

        var files = await context.Provider.FetchAsync(option, CancellationToken.None);
        Assert.AreEqual(2, files.Count);
        Assert.AreEqual("gutenberg-22979-01.mp3", files[0].Filename);
        Assert.AreEqual("gutenberg-22979-02.mp3", files[1].Filename);
    }

    [TestMethod]
    public async Task FetchAsyncForAnAudioBundleOpensEachTrackConnectionLazilyOnlyWhenRead()
    {
        // Regression coverage for a real production incident: eagerly
        // opening every chapter's HTTP request up front left later tracks
        // idle on an open connection while earlier ones were still being
        // written to disk, long enough for a 15-20 chapter audiobook that
        // Gutenberg/Cloudflare closed the idle ones out from under it — an
        // uncaught IOException that silently killed the whole
        // automatic-fulfillment background pass. Fetching a bundle must not
        // issue any HTTP request until each track's stream is actually read,
        // and reading one track must not touch the next track's connection.
        var tracks = new[]
        {
            new GutenbergAudioTrack(new Uri("https://www.gutenberg.org/files/22979/mp3/22979-01.mp3"), ".mp3"),
            new GutenbergAudioTrack(new Uri("https://www.gutenberg.org/files/22979/mp3/22979-02.mp3"), ".mp3"),
            new GutenbergAudioTrack(new Uri("https://www.gutenberg.org/files/22979/mp3/22979-03.mp3"), ".mp3")
        };
        var catalog = new FakeGutenbergAudiobookCatalog { TracksById = { [22979] = tracks } };
        var handler = new RecordingHandler(MatchingSoundResponse);
        var context = new TestContext(handler, enabled: true, catalog: catalog);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);
        var option = options[0];

        var files = await context.Provider.FetchAsync(option, CancellationToken.None);
        Assert.AreEqual(3, files.Count);
        Assert.AreEqual(1, handler.CallCount, "Discovery makes exactly one Gutendex search call; fetching the bundle must not yet open any track connection.");

        for (var index = 0; index < files.Count; index++)
        {
            using var reader = new StreamReader(files[index].Content);
            _ = await reader.ReadToEndAsync();
            Assert.AreEqual(
                2 + index,
                handler.CallCount,
                $"Track {index} should open exactly one connection when read, and no others should have opened yet.");
        }
    }

    [TestMethod]
    public async Task AnAudiobookMatchWithoutASoundMediaTypeIsNotEligible()
    {
        const string notSoundResponse = """
            {
              "count": 1,
              "results": [
                {
                  "id": 1234,
                  "title": "The Hobbit",
                  "media_type": "Text",
                  "authors": [{"name": "Tolkien, J. R. R."}],
                  "formats": {}
                }
              ]
            }
            """;
        var catalog = new FakeGutenbergAudiobookCatalog();
        var context = new TestContext(new RecordingHandler(notSoundResponse), enabled: true, catalog: catalog);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
        Assert.AreEqual(0, catalog.CallCount);
    }

    [TestMethod]
    public async Task AnAudiobookMatchWithNoRdfTracksIsNotEligible()
    {
        var catalog = new FakeGutenbergAudiobookCatalog { TracksById = { [22979] = [] } };
        var context = new TestContext(new RecordingHandler(MatchingSoundResponse), enabled: true, catalog: catalog);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
    }

    [TestMethod]
    public async Task AnAudiobookMatchExceedingTheBundleTrackLimitIsNotEligible()
    {
        var tracks = Enumerable.Range(1, 5)
            .Select(sequence => new GutenbergAudioTrack(
                new Uri($"https://www.gutenberg.org/files/22979/mp3/22979-{sequence:00}.mp3"), ".mp3"))
            .ToArray();
        var catalog = new FakeGutenbergAudiobookCatalog { TracksById = { [22979] = tracks } };
        var policy = new ManualImportPolicy { MaxAudiobookBundleTracks = 4 };
        var context = new TestContext(
            new RecordingHandler(MatchingSoundResponse), enabled: true, catalog: catalog, importPolicy: policy);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Audiobook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
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
    public async Task AnUnavailableCatalogIsReportedAsAProviderFailureRatherThanANoMatch()
    {
        var context = new TestContext(new StatusHandler(HttpStatusCode.ServiceUnavailable), enabled: true);

        var exception = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            context.Provider.FindDirectAcquisitionsAsync(
                Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
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
        Assert.AreEqual("/books/", handler.LastRequestUri?.AbsolutePath);
    }

    [TestMethod]
    public async Task ATitleDifferingOnlyByALeadingArticleStillMatches()
    {
        const string leadingArticleResponse = """
            {
              "count": 1,
              "results": [
                {
                  "id": 2868,
                  "title": "The Green Mummy",
                  "authors": [{"name": "Hume, Fergus"}],
                  "formats": {"application/epub+zip": "https://www.gutenberg.org/ebooks/2868.epub3.images"}
                }
              ]
            }
            """;
        var context = new TestContext(
            new RecordingHandler(leadingArticleResponse), enabled: true, workTitle: "Green Mummy", workAuthor: "Fergus Hume");

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("2868", options[0].ProviderResultId);
    }

    [TestMethod]
    public async Task ATitleDifferingOnlyByAnAmpersandVersusAndStillMatches()
    {
        const string spelledOutResponse = """
            {
              "count": 1,
              "results": [
                {
                  "id": 1513,
                  "title": "Romeo and Juliet",
                  "authors": [{"name": "Shakespeare, William"}],
                  "formats": {"application/epub+zip": "https://www.gutenberg.org/ebooks/1513.epub3.images"}
                }
              ]
            }
            """;
        var context = new TestContext(
            new RecordingHandler(spelledOutResponse), enabled: true, workTitle: "Romeo & Juliet", workAuthor: "William Shakespeare");

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("1513", options[0].ProviderResultId);
    }

    [TestMethod]
    public async Task AGutendexAuthorWithAParentheticalFullNameStillMatches()
    {
        const string expandedAuthorResponse = """
            {
              "count": 1,
              "results": [
                {
                  "id": 22984,
                  "title": "Peter Pan",
                  "authors": [{"name": "Barrie, J. M. (James Matthew)"}],
                  "formats": {"application/epub+zip": "https://www.gutenberg.org/ebooks/22984.epub.noimages"}
                }
              ]
            }
            """;
        var context = new TestContext(
            new RecordingHandler(expandedAuthorResponse), enabled: true, workTitle: "Peter Pan", workAuthor: "J. M. Barrie");

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("22984", options[0].ProviderResultId);
    }

    [TestMethod]
    public async Task ATitleMatchWithADifferentAuthorIsNotEligibleForDirectAcquisition()
    {
        const string differentAuthorResponse = """
            {
              "count": 1,
              "results": [
                {
                  "id": 1234,
                  "title": "The Hobbit",
                  "authors": [{"name": "Somebody Else"}],
                  "formats": {"application/epub+zip": "https://www.gutenberg.org/ebooks/1234.epub.noimages"}
                }
              ]
            }
            """;
        var context = new TestContext(new RecordingHandler(differentAuthorResponse), enabled: true);

        var options = await context.Provider.FindDirectAcquisitionsAsync(
            Guid.NewGuid(), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(0, options.Count);
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

        var files = await context.Provider.FetchAsync(option, CancellationToken.None);

        Assert.AreEqual(1, files.Count);
        Assert.AreEqual("gutenberg-1234.epub", files[0].Filename);
        using var reader = new StreamReader(files[0].Content);
        Assert.AreEqual("epub file bytes", await reader.ReadToEndAsync());
        Assert.AreEqual(
            "https://www.gutenberg.org/ebooks/1234.epub.noimages", handler.LastRequestUri?.ToString());
    }

    private sealed class TestContext
    {
        public TestContext(
            HttpMessageHandler handler,
            bool enabled,
            string workTitle = "The Hobbit",
            string workAuthor = "J. R. R. Tolkien",
            IGutenbergAudiobookCatalog? catalog = null,
            ManualImportPolicy? importPolicy = null)
        {
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://gutendex.com/") };
            var descriptor = new ProviderDescriptor(
                "gutendex",
                "Gutendex (Project Gutenberg)",
                new HashSet<ProviderCapability> { ProviderCapability.DirectAcquisition },
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                DefaultEnabled: false);
            var setting = new ProviderSetting("gutendex", Now);
            setting.SetEnabled(enabled, null, Now);

            Provider = new GutendexProvider(
                httpClient,
                new FakeProviderRegistry(descriptor),
                new FakeProviderSettingsStore(setting),
                new FakeWorkLookup(workTitle, workAuthor),
                catalog ?? new FakeGutenbergAudiobookCatalog(),
                importPolicy ?? new ManualImportPolicy());
        }

        public GutendexProvider Provider { get; }
    }

    private sealed class FakeGutenbergAudiobookCatalog : IGutenbergAudiobookCatalog
    {
        public Dictionary<int, IReadOnlyList<GutenbergAudioTrack>> TracksById { get; } = [];

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<GutenbergAudioTrack>> FindTracksAsync(int gutenbergId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(TracksById.GetValueOrDefault(gutenbergId, []));
        }
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

    private sealed class FakeWorkLookup(string title, string author) : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkSummary?>(new WorkSummary(workId, title, author));
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

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
