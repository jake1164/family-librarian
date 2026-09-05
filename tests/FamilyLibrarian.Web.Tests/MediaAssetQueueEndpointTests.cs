using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Channels;
using FamilyLibrarian.Domain.Security;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// Covers the admin acquisition/security queue: the read surface an
/// administrator needs to discover what's awaiting approval or a retry, since
/// the manual-import and approve/reject endpoints only ever address an asset
/// the caller already knows the id of.
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
    public async Task AnUploadIsAutomaticallyEvaluatedBeforeAppearingInTheQueue()
    {
        // Title/author deliberately don't match the-hobbit's catalog metadata
        // (see EpubAssetIdentityVerifier): a clean scan alone is not enough to
        // leave the queue automatically — it also has to be held for identity
        // review, which is what keeps this asset visible here to assert on.
        // A clean scan that also matches is covered by
        // SecurityGateEndpointTests.ACleanFileIsApprovedByPolicyAndBecomesTrusted.
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

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);

        var entry = queue.Assets.SingleOrDefault(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(entry, "The freshly staged asset should be in the queue.");
        Assert.AreEqual(requestId, entry.RequestId);
        Assert.AreEqual("Unmatched", entry.StorageState);
        Assert.IsNotNull(entry.LatestEvaluation);
        Assert.AreEqual(nameof(SecurityEvaluationStatus.Passed), entry.LatestEvaluation.Status);
    }

    [TestMethod]
    public async Task AnApprovedAssetLeavesTheQueue()
    {
        // The default fixture's title/author match the-hobbit's catalog
        // metadata, so a clean scan is approved by policy and published
        // immediately during the upload itself — there is no separate admin
        // approval step to exercise here anymore.
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

        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);
        Assert.IsFalse(queue.Assets.Any(asset => asset.AssetId == imported.MediaAssetId));

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        Assert.IsNotNull(recent);
        var retained = recent.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(retained.LatestEvaluation);
        Assert.IsNotNull(retained.LatestEvaluation.CompletedAtUtc);
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

        // The fail-closed policy already moved a Failed evaluation's asset to
        // Rejected on its own — it should have left the queue without any
        // further admin action.
        var queue = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/");
        Assert.IsNotNull(queue);
        Assert.IsFalse(queue.Assets.Any(asset => asset.AssetId == imported.MediaAssetId));

        var recent = await client.GetFromJsonAsync<MediaAssetAdminListResponse>("/api/v1/admin/media-assets/recent?limit=100");
        Assert.IsNotNull(recent);
        var retained = recent.Assets.Single(asset => asset.AssetId == imported.MediaAssetId);
        Assert.IsNotNull(retained.LatestEvaluation);
        Assert.IsNotNull(retained.LatestEvaluation.CompletedAtUtc);
    }

    [TestMethod]
    public async Task RecentFilesRequireAdminAndLiveConnectionRequiresSignIn()
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var anonymous = fixture.CreateAnonymousClient();
        using var reader = await fixture.CreateUserClientAsync();
        using var admin = await fixture.CreateAdminClientAsync();
        foreach (var (client, expected) in new[]
        {
            (anonymous, HttpStatusCode.Unauthorized), (reader, HttpStatusCode.Forbidden), (admin, HttpStatusCode.OK)
        })
        {
            using var list = await client.GetAsync("/api/v1/admin/media-assets/recent");
            Assert.AreEqual(expected, list.StatusCode);
            using var negotiate = await client.PostAsync(LiveUpdates.HubPath + "/negotiate?negotiateVersion=1", null);
            Assert.AreEqual(expected == HttpStatusCode.Unauthorized ? expected : HttpStatusCode.OK, negotiate.StatusCode);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(101)]
    public async Task RecentFilesRejectsAnUnboundedLimit(int limit)
    {
        var fixture = WebTestFixture.Require(_fixture);
        using var client = await fixture.CreateAdminClientAsync();
        using var response = await client.GetAsync($"/api/v1/admin/media-assets/recent?limit={limit}");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task LiveUpdatesExposePendingThenCompletedScansAndRecentFilesAreBounded()
    {
        var fixture = WebTestFixture.Require(_fixture);
        var scanner = new PausedScanner();
        await using var factory = CreateFactory(fixture, scanner);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = FamilyLibrarianAppFactory.AdminEmail,
            Password = FamilyLibrarianAppFactory.AdminPassword
        });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        var cookies = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(value => value.Split(';', 2)[0]));
        client.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName,
            await WebTestFixture.GetAntiforgeryTokenAsync(client));
        var (requestId, formatId) = await CreateEbookRequestAsync(client);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, LiveUpdates.HubPath), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookies;
            }).Build();
        var signals = Channel.CreateUnbounded<bool>();
        connection.On<LiveUpdateTopics>(LiveUpdates.Changed, topics =>
        {
            if (topics.HasFlag(LiveUpdateTopics.Security)) signals.Writer.TryWrite(true);
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await connection.StartAsync(timeout.Token);

        using var content = BuildUpload(BuildMinimalEpubBytes(), "live.epub");
        var uploadTask = client.PostAsync(
            $"/api/v1/admin/requests/{requestId}/formats/{formatId}/manual-import", content, timeout.Token);
        try
        {
            await scanner.Started.Task.WaitAsync(timeout.Token);
            await signals.Reader.ReadAsync(timeout.Token);
            var pendingList = await client.GetFromJsonAsync<MediaAssetAdminListResponse>(
                "/api/v1/admin/media-assets/recent?limit=1", timeout.Token);
            Assert.IsNotNull(pendingList);
            Assert.HasCount(1, pendingList.Assets);
            var pending = pendingList.Assets.Single();
            Assert.AreEqual("Processing", pending.StorageState);
            Assert.AreEqual(requestId, pending.RequestId);
            Assert.IsNotNull(pending.LatestEvaluation);
            Assert.AreEqual("Pending", pending.LatestEvaluation.Status);
            Assert.IsNull(pending.LatestEvaluation.CompletedAtUtc);
            while (signals.Reader.TryRead(out _)) { }

            scanner.Release.TrySetResult();
            using var upload = await uploadTask;
            Assert.AreEqual(HttpStatusCode.OK, upload.StatusCode);
            await signals.Reader.ReadAsync(timeout.Token);
            var completedList = await client.GetFromJsonAsync<MediaAssetAdminListResponse>(
                "/api/v1/admin/media-assets/recent?limit=1", timeout.Token);
            Assert.IsNotNull(completedList);
            var completed = completedList.Assets.Single();
            Assert.AreEqual(pending.AssetId, completed.AssetId);
            Assert.IsNotNull(completed.LatestEvaluation);
            Assert.AreEqual("Passed", completed.LatestEvaluation.Status);
            Assert.IsTrue(completed.LatestEvaluation.CompletedAtUtc > completed.LatestEvaluation.CreatedAtUtc);
            Assert.IsTrue(completed.LatestEvaluation.ScanResults.Single().ScannedAtUtc > completed.LatestEvaluation.CreatedAtUtc);
        }
        finally
        {
            scanner.Release.TrySetResult();
            await uploadTask;
        }
    }

    private sealed class PausedScanner : IMalwareScanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "paused";
        public bool IsRequired => true;
        public Task<ScannerHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ScannerHealth(true, "test", null));
        public async Task<ScanOutcome> ScanAsync(Stream content, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new ScanOutcome(ScanResultStatus.Clean, null);
        }
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
            new CreateBookRequestRequest(work.Id, ["Ebook"], null, true, false));
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
