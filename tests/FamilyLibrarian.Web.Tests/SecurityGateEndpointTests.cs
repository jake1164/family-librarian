using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers M10's stated acceptance bar: a clean-path test, a detection test,
/// and a scanner-unavailable test. Every test here swaps in a fake
/// <see cref="IMalwareScanner"/> — real ClamAV reachability is never a
/// precondition for these to pass.
/// </summary>
[TestClass]
public sealed class SecurityGateEndpointTests
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
    public async Task AScannerUnavailableUploadIsActivelyRejectedNotSkipped()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new UnavailableMalwareScanner());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var response = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));

        // Deliberately not the Postgres-unavailable idiom (Assert.Inconclusive):
        // the app must be proven to actively reject, not merely be untestable.
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.IsFalse(await database.MediaAssets.AnyAsync(asset => asset.AssociatedRequestFormatId == formatId));
        Assert.IsFalse(await database.AcquisitionJobs.AnyAsync(job => job.RequestId == requestId));

        var health = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }

    [TestMethod]
    public async Task ACleanFileIsApprovedByPolicyAndBecomesTrusted()
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

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(asset => asset.Id == imported.MediaAssetId);
        Assert.AreEqual(MediaAssetStorageState.Trusted, asset.StorageState);
        var approval = await database.SecurityEvaluations
            .Where(evaluation => evaluation.AssetId == imported.MediaAssetId)
            .SelectMany(evaluation => evaluation.Approvals)
            .SingleAsync();
        Assert.AreEqual(ApprovalActorType.Policy, approval.ActorType);
        Assert.AreEqual("clean-security-evaluation-v1", approval.PolicyName);
    }

    [TestMethod]
    public async Task ACleanButWrongEpubIsHeldUnmatchedAndNeverApproved()
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
            BuildUpload(EpubTestFixture.BuildMinimalEpubBytes("A Different Book", "Someone Else"), "book.epub"));
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(asset => asset.Id == imported.MediaAssetId);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, asset.StorageState);
        Assert.IsFalse(await database.SecurityEvaluations
            .Where(evaluation => evaluation.AssetId == imported.MediaAssetId)
            .SelectMany(evaluation => evaluation.Approvals)
            .AnyAsync());
    }

    [TestMethod]
    public async Task ADetectedThreatIsRejectedAndNeverReachesTrusted()
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
        Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
        var imported = await upload.Content.ReadFromJsonAsync<ManualImportResultResponse>();
        Assert.IsNotNull(imported);

        // Nobody — not even an administrator — can approve a failed evaluation.
        var approve = await client.PostAsJsonAsync(
            $"/api/v1/admin/media-assets/{imported.MediaAssetId}/approve",
            new ApprovalDecisionRequest("attempted override"));
        Assert.AreEqual(HttpStatusCode.BadRequest, approve.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(asset => asset.Id == imported.MediaAssetId);
        Assert.AreEqual(MediaAssetStorageState.Rejected, asset.StorageState);
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

    [TestMethod]
    public async Task AScannerFailureDuringEvaluationReturnsProblemDetailsNotTheSpaShell()
    {
        // F8: app.UseExceptionHandler("/error") used to re-execute the pipeline
        // at a path nothing mapped, which fell through to
        // MapFallbackToFile("index.html") — an unhandled exception came back
        // as the SPA's HTML shell, which every JSON-expecting client then
        // failed to parse. This proves the real host now answers with a
        // ProblemDetails body instead, through the same scanner failure F3's
        // recovery path is built around.
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = CreateFactory(fixture, new ThrowingMalwareScanner());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await SignInAsAdminAsync(client);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(client);
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        var upload = await client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import",
            BuildUpload(BuildMinimalEpubBytes(), "book.epub"));
        Assert.AreEqual(HttpStatusCode.InternalServerError, upload.StatusCode);
        Assert.AreEqual("application/problem+json", upload.Content.Headers.ContentType?.MediaType);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await database.MediaAssets.SingleAsync(asset => asset.AssociatedRequestFormatId == formatId);
        Assert.AreEqual(MediaAssetStorageState.Quarantine, asset.StorageState);
    }

    private static byte[] BuildMinimalEpubBytes() => EpubTestFixture.BuildMinimalEpubBytes();

    /// <summary>
    /// Always reports unhealthy; throws if <see cref="ScanAsync"/> is ever
    /// called, proving <see cref="IAcquisitionBoundaryGuard"/> short-circuits
    /// before scanning is attempted.
    /// </summary>
    private sealed class UnavailableMalwareScanner : IMalwareScanner
    {
        public string Id => "clamav";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(false, null, "Simulated outage."));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "ScanAsync must never be called when CheckHealthAsync reports unhealthy.");
    }

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

    /// <summary>Reports healthy, then fails mid-scan — the shape a dropped clamd connection actually takes.</summary>
    private sealed class ThrowingMalwareScanner : IMalwareScanner
    {
        public string Id => "clamav";

        public bool IsRequired => true;

        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "fake-1.0", null));

        public Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken) =>
            throw new IOException("Simulated connection reset mid-stream.");
    }
}
