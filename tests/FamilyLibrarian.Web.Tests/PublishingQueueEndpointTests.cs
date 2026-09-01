using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the end-to-end publishing pipeline through the real HTTP endpoints:
/// approving a manually imported asset triggers a publish attempt, the result
/// appears in the admin queue, and Recheck can move it forward.
/// </summary>
[TestClass]
public sealed class PublishingQueueEndpointTests
{
    private static WebTestFixture? _fixture;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext testContext)
    {
        ArgumentNullException.ThrowIfNull(testContext);
        _fixture = await WebTestFixture.CreateAsync();
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AnApprovedEbookAppearsInTheQueueAwaitingVerificationByDefault()
    {
        // The shared fixture's default fakes (AlwaysSucceedsCwaIngestTransport,
        // AlwaysEmptyCwaCatalogClient) mean the handoff always succeeds but the
        // immediate catalog check never finds it — exactly the "asynchronous
        // ingest, nothing confirmed yet" case Recheck exists for.
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureCwaAsync(client);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);
        var assetId = await ManualImportAndApproveAsync(client, requestId, formatId);

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queue);
        var import = queue.LibraryImports.SingleOrDefault(entry => entry.RequestId == requestId);
        Assert.IsNotNull(import, "The approved ebook should have a library-import queue entry.");
        Assert.AreEqual("AwaitingVerification", import.Status);
        Assert.IsNull(import.ExternalBookId);

        // Recheck with the still-empty catalog fake leaves it exactly where it was.
        var recheck = await client.PostAsync($"/api/v1/admin/publishing/library-imports/{import.Id}/recheck", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, recheck.StatusCode);

        var queueAfter = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var importAfter = queueAfter!.LibraryImports.Single(entry => entry.Id == import.Id);
        Assert.AreEqual("AwaitingVerification", importAfter.Status);
    }

    [TestMethod]
    public async Task RecheckMarksTheImportAvailableOnceTheCatalogFindsIt()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<ICwaCatalogClient>();
                services.AddSingleton<ICwaCatalogClient>(new DeterministicCatalogClient(bookIdOnFirstCall: null));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureCwaAsync(client);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);
        await ManualImportAndApproveAsync(client, requestId, formatId);

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var import = queue!.LibraryImports.Single(entry => entry.RequestId == requestId);
        Assert.AreEqual("AwaitingVerification", import.Status);

        // Flip the fake to now report the book found, then recheck.
        var catalogClient = (DeterministicCatalogClient)factory.Services.GetRequiredService<ICwaCatalogClient>();
        catalogClient.NextBookId = "123";

        var recheck = await client.PostAsync($"/api/v1/admin/publishing/library-imports/{import.Id}/recheck", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, recheck.StatusCode);

        // The queue is a status view, not a to-do list: an Available entry stays
        // visible (with its book id) rather than disappearing, since there is no
        // other admin surface where a successful publish shows up.
        var queueAfter = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        var importAfter = queueAfter!.LibraryImports.SingleOrDefault(entry => entry.Id == import.Id);
        Assert.IsNotNull(importAfter);
        Assert.AreEqual("Available", importAfter.Status);
        Assert.AreEqual("123", importAfter.ExternalBookId);
    }

    [TestMethod]
    public async Task AnApprovedAudiobookIsDeliveredImmediatelyByDefault()
    {
        // AlwaysEmptyAudiobookshelfApiClient reports no existing item, then a
        // successful upload with a generated id — the ordinary "clean" path.
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        await ConfigureAudiobookshelfAsync(client);
        var (requestId, formatId) = await CreateAudiobookRequestAsync(client);
        await ManualImportAudiobookAndApproveAsync(client, requestId, formatId);

        var queue = await client.GetFromJsonAsync<PublishingQueueResponse>("/api/v1/admin/publishing/queue");
        Assert.IsNotNull(queue);
        var delivery = queue.Deliveries.SingleOrDefault(entry => entry.RequestId == requestId);
        Assert.IsNotNull(delivery, "The approved audiobook should have a delivery queue entry.");
        Assert.AreEqual("Delivered", delivery.Status);
        Assert.IsNotNull(delivery.ExternalItemId);
    }

    private static async Task ConfigureCwaAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/",
            new SetCwaSettingsRequest(
                "Local", "/data/cwa-ingest-test", null, null, null, null, "PrivateKey",
                "https://cwa.example.test", null));
        response.EnsureSuccessStatusCode();

        // Enabling requires a passing connection test for the saved configuration
        // (docs/01 §12.1.1) -- FamilyLibrarianAppFactory registers a default-safe
        // ICwaConnectionTester double, so this succeeds without a reachable CWA.
        var test = await client.PostAsJsonAsync("/api/v1/admin/publishing/cwa/test", new { });
        test.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/cwa/enabled", new SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task ConfigureAudiobookshelfAsync(HttpClient client)
    {
        var response = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/",
            new SetAudiobookshelfSettingsRequest("https://abs.example.test", "lib-1", "folder-1"));
        response.EnsureSuccessStatusCode();

        var enabled = await client.PutAsJsonAsync(
            "/api/v1/admin/publishing/audiobookshelf/enabled", new SetPublishingEnabledRequest(true));
        enabled.EnsureSuccessStatusCode();
    }

    private static async Task SignInAsAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The uploaded file's title/author match the-hobbit's catalog metadata
    // (ebook) or has no identity verifier at all (audiobook — see
    // EpubAssetIdentityVerifier.Supports), so a clean scan is approved by
    // policy and published during the upload itself; no separate admin
    // approval call is needed.
    private static async Task<Guid> ManualImportAndApproveAsync(HttpClient client, Guid requestId, Guid formatId)
    {
        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        return imported.MediaAssetId;
    }

    private static async Task<Guid> ManualImportAudiobookAndApproveAsync(HttpClient client, Guid requestId, Guid formatId)
    {
        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalMp3Bytes(), "book.mp3"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        return imported.MediaAssetId;
    }

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private static async Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(HttpClient client) =>
        await CreateRequestAsync(client, "Ebook");

    private static async Task<(Guid RequestId, Guid FormatId)> CreateAudiobookRequestAsync(HttpClient client) =>
        await CreateRequestAsync(client, "Audiobook");

    private static async Task<(Guid RequestId, Guid FormatId)> CreateRequestAsync(HttpClient client, string mediaType)
    {
        var resolve = await client.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(work.Id, [mediaType], null, true, false));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == mediaType);
        return (request.Id, format.FormatId);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();

    private static byte[] BuildMinimalMp3Bytes()
    {
        // Beyond SignatureFileTypeDetector's magic-byte sniff, AudioValidator
        // now requires two chained, structurally valid MPEG frames -- a bare
        // ID3 header with no real frame behind it (the previous fixture) no
        // longer passes. MPEG1 Audio Layer III, bitrate index 9 (128 kbps),
        // sample-rate index 0 (44100 Hz), no padding.
        var id3Header = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        return [.. id3Header, .. BuildMp3Frame(), .. BuildMp3Frame()];
    }

    private static byte[] BuildMp3Frame()
    {
        const int bitrateKbps = 128;
        const int sampleRateHz = 44100;
        var frame = new byte[144 * bitrateKbps * 1000 / sampleRateHz];
        frame[0] = 0xFF;
        frame[1] = 0xFB; // sync(3)=111, version(2)=11 (MPEG1), layer(2)=01 (Layer III), protection=1
        frame[2] = 9 << 4; // bitrate index 9, sample-rate index 0, no padding
        return frame;
    }

    private sealed class DeterministicCatalogClient(string? bookIdOnFirstCall) : ICwaCatalogClient
    {
        public string? NextBookId { get; set; } = bookIdOnFirstCall;

        public Task<BookMatchResult> FindBookIdAsync(
            string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken) =>
            Task.FromResult(NextBookId is null
                ? BookMatchResult.NoMatchResult
                : BookMatchResult.Match(new CandidateBook(NextBookId, title, author)));
    }
}
