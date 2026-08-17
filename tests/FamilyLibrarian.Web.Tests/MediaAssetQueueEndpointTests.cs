using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Security;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the admin acquisition/security queue: the read surface an
/// administrator needs to discover what's sitting in quarantine or awaiting
/// approval, since the manual-import and evaluate/approve/reject endpoints
/// only ever address an asset the caller already knows the id of.
/// </summary>
[TestClass]
public sealed class MediaAssetQueueEndpointTests
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
    public async Task AQuarantinedUploadAppearsInTheQueueWithNoEvaluationYet()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);

        var entry = queue.Assets.SingleOrDefault(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(entry, "The freshly staged asset should be in the queue.");
        Assert.AreEqual(requestId, entry.RequestId);
        Assert.AreEqual("Quarantine", entry.StorageState);
        Assert.IsNull(entry.LatestEvaluation);
    }

    [TestMethod]
    public async Task AnApprovedAssetLeavesTheQueue()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Clean));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var evaluate = await client.PostAsync(
            $"/api/v1/admin/media-assets/{imported.MediaAssetId}/evaluate", content: null);
        Assert.AreEqual(HttpStatusCode.OK, evaluate.StatusCode);

        var queueBeforeApproval = await client.GetFromJsonAsync<MediaAssetAdminListResponse>(
            "/api/v1/admin/media-assets/");
        Assert.IsNotNull(queueBeforeApproval);
        var pending = queueBeforeApproval.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.AreEqual("Processing", pending.StorageState);
        Assert.IsNotNull(pending.LatestEvaluation);
        Assert.AreEqual(nameof(SecurityEvaluationStatus.Passed), pending.LatestEvaluation.Status);
        Assert.AreEqual(1, pending.LatestEvaluation.ScanResults.Count);

        var approve = await client.PostAsJsonAsync(
            $"/api/v1/admin/media-assets/{imported.MediaAssetId}/approve",
            new ApprovalDecisionRequest(null));
        Assert.AreEqual(HttpStatusCode.NoContent, approve.StatusCode);

        var queueAfterApproval = await client.GetFromJsonAsync<MediaAssetAdminListResponse>(
            "/api/v1/admin/media-assets/");
        Assert.IsNotNull(queueAfterApproval);
        Assert.IsFalse(queueAfterApproval.Assets.Any(asset => asset.AssetId == imported.MediaAssetId));
    }

    [TestMethod]
    public async Task ARejectedAssetLeavesTheQueue()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(
            fixture, new DeterministicFakeMalwareScanner(ScanResultStatus.Detected, "Eicar-Test-Signature"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        var evaluate = await client.PostAsync(
            $"/api/v1/admin/media-assets/{imported.MediaAssetId}/evaluate", content: null);
        Assert.AreEqual(HttpStatusCode.OK, evaluate.StatusCode);

        // The fail-closed policy already moved a Failed evaluation's asset to
        // Rejected on its own — it should have left the queue without any
        // further admin action.
        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);
        Assert.IsFalse(queue.Assets.Any(asset => asset.AssetId == imported.MediaAssetId));
    }

    private static FamilyLibrarianAppFactory CreateFactory(WebTestFixture fixture, IMalwareScanner scanner) =>
        new(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IMalwareScanner>();
                services.AddSingleton(scanner);
            });

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

    private static MultipartFormDataContent BuildUpload(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        return form;
    }

    private static async Task<(Guid RequestId, Guid FormatId)> CreateEbookRequestAsync(HttpClient client)
    {
        var resolve = await client.PostAsync(
            "/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        resolve.EnsureSuccessStatusCode();
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);

        var created = await client.PostAsJsonAsync(
            "/api/v1/requests/",
            new CreateBookRequestRequest(work.Id, ["Ebook"], null, true));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var request = await created.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        var format = request.Formats.Single(format => format.MediaType == "Ebook");
        return (request.Id, format.FormatId);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();

    private sealed class DeterministicFakeMalwareScanner(ScanResultStatus status, string? threatName = null)
        : IMalwareScanner
    {
        public string Id => "clamav";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "fake-1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            Task.FromResult(new ScanOutcome(status, threatName));
    }
}
