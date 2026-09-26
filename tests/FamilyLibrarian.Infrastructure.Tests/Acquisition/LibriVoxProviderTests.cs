using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Acquisition;
using FamilyLibrarian.Infrastructure.LibriVox;
using FamilyLibrarian.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class LibriVoxProviderTests
{
    private static readonly string[] ExpectedProjectIds = ["17", "18"];
    private static readonly string[] SoloReaderNames = ["Jane Reader"];
    private static readonly string[] CollaborativeReaderNames = ["Jane Reader", "John Doe"];
    [TestMethod]
    public async Task ApiSearchUsesTitleOnlyAndParsesExtendedRecordingEvidence()
    {
        var handler = new StubHandler(_ => BooksResponse(RecordingJson("17", "Moby Dick", "English")));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://librivox.org/") };
        var api = new LibriVoxApiClient(client, new LibriVoxRequestThrottle());

        var results = await api.SearchByTitleAsync("Moby Dick & the sea", CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("17", results[0].Id);
        Assert.AreEqual("Herman Melville", results[0].Authors[0]);
        Assert.AreEqual("Jane Reader", results[0].Readers[0]);
        Assert.AreEqual("Fiction", results[0].Genres[0]);
        Assert.AreEqual("English", results[0].Language);
        Assert.AreEqual(3600L, results[0].DurationSeconds);
        Assert.IsTrue(results[0].SectionTitles.Contains("Chapter 1"));
        var query = handler.LastRequest!.RequestUri!.Query;
        StringAssert.Contains(query, "title=Moby%20Dick%20%26%20the%20sea");
        StringAssert.Contains(query, "extended=1");
        Assert.IsFalse(query.Contains("author=", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ApiSearchHonorsRateLimitAndRetriesOnce()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }
            return BooksResponse(RecordingJson("17", "Moby Dick", "English"));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://librivox.org/") };
        var api = new LibriVoxApiClient(client, new LibriVoxRequestThrottle());

        var results = await api.SearchByTitleAsync("Moby Dick", CancellationToken.None);

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task ProviderReturnsDistinctMatchedAudiobookOptionsAndRejectsOtherMediaTypes()
    {
        var first = RecordingJson("17", "Moby Dick", "English");
        var second = RecordingJson("18", "Moby Dick", "English").Replace("Jane Reader", "John Reader", StringComparison.Ordinal);
        var handler = new StubHandler(_ => BooksResponse(first, second));
        using var apiHttp = new HttpClient(handler) { BaseAddress = new Uri("https://librivox.org/") };
        var api = new LibriVoxApiClient(apiHttp, new LibriVoxRequestThrottle());
        using var downloadHttp = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var provider = CreateProvider(api, downloadHttp);

        var options = await provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None);
        var ebooks = await provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Ebook, CancellationToken.None);

        Assert.AreEqual(2, options.Count);
        CollectionAssert.AreEqual(ExpectedProjectIds, options.Select(option => option.ProviderResultId).ToArray());
        Assert.AreEqual("mp3", options[0].Format);
        Assert.AreEqual("Jane Reader", options[0].Narrator);
        Assert.AreEqual(NarrationKind.Human, options[0].NarrationKind);
        Assert.AreEqual(1, options[0].PartCount);
        Assert.AreEqual(0, ebooks.Count);
    }

    [TestMethod]
    public async Task SoloReaderRecordingIsPresentedAsHumanNarrationWithoutAReadersList()
    {
        var handler = new StubHandler(_ => BooksResponse(RecordingJson("17", "Moby Dick", "English")));
        using var apiHttp = new HttpClient(handler) { BaseAddress = new Uri("https://librivox.org/") };
        var provider = CreateProvider(new LibriVoxApiClient(apiHttp, new LibriVoxRequestThrottle()),
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        var option = (await provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None)).Single();

        Assert.AreEqual(NarrationKind.Human, option.NarrationKind);
        CollectionAssert.AreEqual(SoloReaderNames, option.NarratorNames!.ToArray());
        var details = RequestReviewCandidatePresentation.BuildDetails(option) ?? string.Empty;
        StringAssert.Contains(details, "Human narration — Jane Reader");
        Assert.IsFalse(details.Contains("Readers:", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CollaborativeRecordingListsEveryReaderInsteadOfFlatteningToOneName()
    {
        var handler = new StubHandler(_ => BooksResponse(CollaborativeRecordingJson("19", "Moby Dick")));
        using var apiHttp = new HttpClient(handler) { BaseAddress = new Uri("https://librivox.org/") };
        var provider = CreateProvider(new LibriVoxApiClient(apiHttp, new LibriVoxRequestThrottle()),
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));

        var option = (await provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None)).Single();

        Assert.AreEqual(NarrationKind.Human, option.NarrationKind);
        CollectionAssert.AreEqual(CollaborativeReaderNames, option.NarratorNames!.ToArray());
        var details = RequestReviewCandidatePresentation.BuildDetails(option) ?? string.Empty;
        StringAssert.Contains(details, "Human narration — collaborative");
        StringAssert.Contains(details, "Readers: Jane Reader, John Doe");
    }

    [TestMethod]
    public async Task FetchDownloadsWholeZipAndReturnsMp3TrackStreams()
    {
        var zip = CreateZip(("folder/01-chapter.mp3", "audio-one"), ("cover.jpg", "cover"), ("readme.txt", "notes"));
        var apiHandler = new StubHandler(_ => BooksResponse(RecordingJson("17", "Moby Dick", "English")));
        using var apiHttp = new HttpClient(apiHandler) { BaseAddress = new Uri("https://librivox.org/") };
        var downloadHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip)
        });
        using var downloadHttp = new HttpClient(downloadHandler);
        var provider = CreateProvider(new LibriVoxApiClient(apiHttp, new LibriVoxRequestThrottle()), downloadHttp);
        var option = (await provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None)).Single();

        var files = await provider.FetchAsync(option, CancellationToken.None);
        try
        {
            Assert.AreEqual(1, files.Count);
            Assert.AreEqual("librivox-17-001.mp3", files[0].Filename);
            using var output = new MemoryStream();
            await files[0].Content.CopyToAsync(output);
            Assert.AreEqual("audio-one", Encoding.UTF8.GetString(output.ToArray()));
            Assert.IsTrue(downloadHandler.LastRequest!.RequestUri!.Host.EndsWith("archive.org", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var file in files) await file.Content.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task FetchRejectsUnsafeArchivePaths()
    {
        var zip = CreateZip(("../outside.mp3", "audio-one"));
        var apiHandler = new StubHandler(_ => BooksResponse(RecordingJson("17", "Moby Dick", "English")));
        using var apiHttp = new HttpClient(apiHandler) { BaseAddress = new Uri("https://librivox.org/") };
        using var downloadHttp = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(zip)
        }));
        var provider = CreateProvider(new LibriVoxApiClient(apiHttp, new LibriVoxRequestThrottle()), downloadHttp);
        var option = (await provider.FindDirectAcquisitionsAsync(
            new BookIdentity("Moby Dick", "Herman Melville", []), RequestMediaType.Audiobook, CancellationToken.None)).Single();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => provider.FetchAsync(option, CancellationToken.None));
    }

    private static LibriVoxProvider CreateProvider(LibriVoxApiClient api, HttpClient downloadClient) => new(
        api, downloadClient, new FakeRegistry(), new FakeSettingsStore(), new FakeWorkLookup(),
        new DeterministicBookMatcher(), new ManualImportPolicy(),
        new LibriVoxDownloadWorkspace(Options.Create(new StorageOptions { RootPath = Path.GetTempPath() })));

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BooksResponse(params string[] recordings) =>
        JsonResponse($"{{\"books\":[{string.Join(',', recordings)}]}}");

    private static byte[] CreateZip(params (string Name, string Text)[] files)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    // Mirrors LibriVox's actual extended=1 response shape (confirmed against the
    // live API): authors use first_name/last_name, but section readers use
    // reader_id/display_name -- not first_name/last_name -- and genres is a
    // plural array of {id, name} objects, not a singular "genre" key. A fixture
    // that drifts from this shape would let the parser regress silently again.
    private static string RecordingJson(string id, string title, string language) =>
        $$"""{"id":"{{id}}","title":"{{title}}","url_zip_file":"https://archive.org/download/example.zip","url_librivox":"https://librivox.org/moby-dick/","language":"{{language}}","copyright_year":"1851","num_sections":"1","totaltimesecs":3600,"authors":[{"id":"155","first_name":"Herman","last_name":"Melville","dob":"1819","dod":"1891"}],"sections":[{"title":"Chapter 1","readers":[{"reader_id":"168","display_name":"Jane Reader"}]}],"genres":[{"id":"27","name":"Fiction"}],"coverart_thumbnail":"https://archive.org/cover.jpg"}""";

    private static string CollaborativeRecordingJson(string id, string title) =>
        $$"""{"id":"{{id}}","title":"{{title}}","url_zip_file":"https://archive.org/download/example.zip","url_librivox":"https://librivox.org/example/","language":"English","copyright_year":"1851","num_sections":"2","totaltimesecs":3600,"authors":[{"id":"155","first_name":"Herman","last_name":"Melville"}],"sections":[{"title":"Chapter 1","readers":[{"reader_id":"168","display_name":"Jane Reader"}]},{"title":"Chapter 2","readers":[{"reader_id":"169","display_name":"John Doe"}]}],"genres":[{"id":"27","name":"Fiction"}],"coverart_thumbnail":"https://archive.org/cover.jpg"}""";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response(request));
        }
    }

    private sealed class FakeRegistry : IProviderRegistry
    {
        private readonly ProviderDescriptor descriptor = new(
            ProviderRegistry.LibriVoxProviderId, "LibriVox", new HashSet<ProviderCapability>(), false, false, true);
        public IReadOnlyList<ProviderDescriptor> GetInstalledProviders() => [descriptor];
        public ProviderDescriptor? Find(string providerId) => providerId == descriptor.Id ? descriptor : null;
    }

    private sealed class FakeSettingsStore : IProviderSettingsStore
    {
        public Task<IReadOnlyList<ProviderSetting>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderSetting>>([]);
        public Task<ProviderSetting?> FindAsync(string providerId, CancellationToken cancellationToken) => Task.FromResult<ProviderSetting?>(null);
        public Task<ProviderSetting> GetOrCreateAsync(string providerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeWorkLookup : IWorkLookup
    {
        public Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken) => Task.FromResult<WorkSummary?>(null);
    }
}
