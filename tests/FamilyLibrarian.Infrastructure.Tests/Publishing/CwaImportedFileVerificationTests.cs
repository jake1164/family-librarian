using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Infrastructure.Publishing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyLibrarian.Infrastructure.Tests.Publishing;

[TestClass]
public sealed class CwaImportedFileVerificationTests
{
    private static readonly byte[] ApprovedBytes = Encoding.UTF8.GetBytes("The approved EPUB bytes.");
    private static string Digest => Convert.ToHexString(SHA256.HashData(ApprovedBytes));

    [TestMethod]
    [DataRow("Mara Quillington")]
    [DataRow("Unknown")]
    public async Task ExactBytesConfirmTheApprovedFileDespiteDifferentOrAbsentByline(string byline)
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", byline));
        context.Handler.Files["42"] = ApprovedBytes;

        // Ordinary ownership continues to require the canonical author.
        var owned = await context.Client.FindBookIdAsync("Cinder Gate", "Mara Quillon", [], CancellationToken.None);
        Assert.AreEqual(BookMatchDecision.NoMatch, owned.Decision);
        var result = await context.FindAsync();

        Assert.AreEqual(BookMatchDecision.Match, result.Decision);
        Assert.AreEqual("42", result.MatchedId);
        Assert.IsTrue(context.Handler.Requests.Any(uri => uri.AbsolutePath == "/library/opds/download/42/epub/"));
    }

    [TestMethod]
    [DataRow("wrong-bytes")]
    [DataRow("short")]
    [DataRow("oversized")]
    [DataRow("wrong-format")]
    [DataRow("wrong-language")]
    [DataRow("wrong-title")]
    [DataRow("unavailable")]
    public async Task MetadataOrAFilePrefixCannotSubstituteForTheApprovedFile(string failure)
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Mara Quillington",
            format: failure == "wrong-format" ? "mobi" : "epub",
            language: failure == "wrong-language" ? "fra" : "eng",
            title: failure == "wrong-title" ? "Other Gate" : "Cinder Gate"));
        context.Handler.Files["42"] = failure switch
        {
            "wrong-bytes" => Enumerable.Repeat((byte)'x', ApprovedBytes.Length).ToArray(),
            "short" => ApprovedBytes[..^1],
            "oversized" => [.. ApprovedBytes, 0],
            _ => ApprovedBytes
        };
        context.Handler.Unavailable = failure == "unavailable";

        Assert.AreEqual(BookMatchDecision.NoMatch, (await context.FindAsync()).Decision);
    }

    [TestMethod]
    public async Task MultipleByteIdenticalCatalogEntriesRemainAmbiguous()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown"), Entry("43", "Unknown"));
        context.Handler.Files["42"] = ApprovedBytes;
        context.Handler.Files["43"] = ApprovedBytes;

        var result = await context.FindAsync();

        Assert.AreEqual(BookMatchDecision.Ambiguous, result.Decision);
        Assert.IsNull(result.MatchedId);
    }

    [TestMethod]
    public async Task OnlyTheByteMatchingEditionIsSelectedAndRepeatedFeedEntriesAreDeduplicated()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown"), Entry("43", "Unknown"));
        context.Handler.Files["42"] = [1, 2, 3];
        context.Handler.Files["43"] = ApprovedBytes;

        Assert.AreEqual("43", (await context.FindAsync()).MatchedId);
        Assert.AreEqual(2, context.Handler.Downloads);
    }

    [TestMethod]
    public async Task CandidateOverflowCannotHideAnotherMatchingEntry()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Enumerable.Range(1, 9).Select(id => Entry(id.ToString(CultureInfo.InvariantCulture), "Unknown")).ToArray());
        context.Handler.Files["1"] = ApprovedBytes;

        Assert.AreEqual(BookMatchDecision.NoMatch, (await context.FindAsync()).Decision);
        Assert.AreEqual(0, context.Handler.Downloads);
    }

    [TestMethod]
    public async Task AcceptedLanguageIsStillRequiredForExactBytes()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown", language: "fra"));
        context.Handler.Files["42"] = ApprovedBytes;
        Assert.AreEqual("42", (await context.FindAsync("fr")).MatchedId);
    }

    [TestMethod]
    public async Task FeedAcquisitionUrlsCannotSendCredentialsToAnotherHost()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown").Replace(
            "/library/opds/download/42/epub/", "https://other.example.test/opds/download/42/epub/", StringComparison.Ordinal));
        context.Handler.Files["42"] = ApprovedBytes;

        Assert.AreEqual(BookMatchDecision.NoMatch, (await context.FindAsync()).Decision);
        Assert.IsTrue(context.Handler.Requests.All(uri => uri.Host == "cwa.example.test"));
    }

    [TestMethod]
    [DataRow(-1, true)]
    [DataRow(0, false)]
    [DataRow(1, false)]
    public async Task ActualStreamLengthIsCheckedWhenHttpSizeIsUnknownOrMisleading(int extraBytes, bool matches)
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown"));
        context.Handler.StreamBytes = extraBytes == -1 ? ApprovedBytes : [.. ApprovedBytes, (byte)'x'];
        context.Handler.StreamLength = extraBytes == 0 ? ApprovedBytes.Length : null;

        Assert.AreEqual(matches ? BookMatchDecision.Match : BookMatchDecision.NoMatch, (await context.FindAsync()).Decision);
        Assert.IsTrue(context.Handler.StreamDisposed);
    }

    [TestMethod]
    public async Task AnInterruptedDownloadCannotConfirmAFile()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown"));
        context.Handler.StreamBytes = ApprovedBytes;
        context.Handler.ThrowDuringRead = true;

        Assert.AreEqual(BookMatchDecision.NoMatch, (await context.FindAsync()).Decision);
        Assert.IsTrue(context.Handler.StreamDisposed);
    }

    [TestMethod]
    public async Task CallerCancellationIsNotConvertedIntoNoMatch()
    {
        var context = new Context();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Client.FindImportedFileAsync(
            "Cinder Gate", ".epub", ApprovedBytes.Length, Digest, cancellation.Token));
    }

    [TestMethod]
    public async Task AFailedSecondDownloadDoesNotMakeTheFirstLookUnique()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown"), Entry("43", "Unknown"));
        context.Handler.Files["42"] = ApprovedBytes;
        context.Handler.UnavailableId = "43";

        Assert.AreEqual(BookMatchDecision.NoMatch, (await context.FindAsync()).Decision);
    }

    [TestMethod]
    public async Task RecentBooksCanDiscoverAnImportedFileWhenTitleSearchMisses()
    {
        var context = new Context();
        context.Handler.Feed = Feed(Entry("42", "Unknown"));
        context.Handler.RecentOnly = true;
        context.Handler.Files["42"] = ApprovedBytes;

        Assert.AreEqual("42", (await context.FindAsync()).MatchedId);
    }

    private static string Feed(params string[] entries) =>
        $"<feed xmlns='http://www.w3.org/2005/Atom' xmlns:dc='http://purl.org/dc/terms/'>{string.Concat(entries)}</feed>";

    private static string Entry(string id, string author, string format = "epub", string language = "eng", string title = "Cinder Gate") =>
        $"<entry><title>{title}</title><author><name>{author}</name></author><dc:language>{language}</dc:language>" +
        $"<link rel='http://opds-spec.org/acquisition' href='/library/opds/download/{id}/{format}/'/></entry>";

    private sealed class Context
    {
        public Handler Handler { get; } = new();
        public CwaCatalogClient Client { get; }

        public Context()
        {
            Client = new CwaCatalogClient(new ClientFactory(Handler), new SettingsStore(), new Protector(),
                new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()),
                NullLogger<CwaCatalogClient>.Instance);
        }

        public Task<BookMatchResult> FindAsync(string? language = null) => Client.FindImportedFileAsync(
            "Cinder Gate", ".epub", ApprovedBytes.Length, Digest, CancellationToken.None, language);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string Feed { get; set; } = CwaImportedFileVerificationTests.Feed();
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<Uri> Requests { get; } = [];
        public bool Unavailable { get; set; }
        public string? UnavailableId { get; set; }
        public bool RecentOnly { get; set; }
        public byte[]? StreamBytes { get; set; }
        public long? StreamLength { get; set; }
        public bool ThrowDuringRead { get; set; }
        public bool StreamDisposed { get; set; }
        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            if (uri.AbsolutePath.Contains("/opds/download/", StringComparison.Ordinal))
            {
                Downloads++;
                var id = uri.Segments[^2].Trim('/');
                return Task.FromResult(new HttpResponseMessage(Unavailable || UnavailableId == id ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                {
                    Content = StreamBytes is null ? new ByteArrayContent(Files.GetValueOrDefault(id, [])) :
                        new UnknownLengthContent(new ObservedStream(StreamBytes, this), StreamLength)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RecentOnly && !uri.AbsolutePath.EndsWith("/new", StringComparison.Ordinal)
                    ? CwaImportedFileVerificationTests.Feed() : Feed)
            });
        }
    }

    private sealed class ObservedStream(byte[] bytes, Handler handler) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            handler.ThrowDuringRead ? ValueTask.FromException<int>(new IOException("Interrupted response")) :
                base.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            handler.StreamDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthContent(Stream stream, long? advertisedLength) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override bool TryComputeLength(out long length)
        {
            length = advertisedLength ?? 0;
            return advertisedLength.HasValue;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) stream.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ClientFactory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class SettingsStore : ICwaSettingsStore
    {
        private readonly CwaSettings _settings = CreateSettings();
        private static CwaSettings CreateSettings()
        {
            var settings = new CwaSettings(DateTimeOffset.UtcNow);
            settings.SetSettings(CwaTransportMode.Local, "/ingest", null, null, null, null,
                CwaSftpAuthenticationMode.PrivateKey, "https://cwa.example.test/library", null, "operator", null, null, DateTimeOffset.UtcNow);
            return settings;
        }
        public Task<CwaSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult<CwaSettings?>(_settings);
        public Task<CwaSettings> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult(_settings);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Protector : ICredentialProtector
    {
        public int FormatVersion => 1;
        public string Protect(string providerId, string plaintext) => plaintext;
        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => "test-only-password";
    }
}
