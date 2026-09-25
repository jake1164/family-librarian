using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// HUMAN-ACQ-1 Phase 1/2: the admin start/fallback/cancel control endpoints
/// (<c>/api/v1/admin/requests/provider-interactions/{jobId}/...</c>) had no
/// dedicated test coverage before this file, despite being an
/// administrator-only, security-relevant surface -- closed alongside adding
/// the Phase 3 view endpoint that shares this same code path.
/// </summary>
[TestClass]
public sealed class ProviderInteractionEndpointTests
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
    public async Task StartingAWaitingInteractionSucceedsAndRecordsTheSessionStart()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);
        using var admin = await CreateAdminClientAsync(factory);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await database.ProviderAcquisitionJobs.SingleAsync(j => j.Id == jobId);
        Assert.IsNotNull(job.InteractionViewSessionStartedAtUtc);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Waiting, job.LifecycleState);
    }

    [TestMethod]
    public async Task UsingFallbackOnAWaitingInteractionSucceeds()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);
        using var admin = await CreateAdminClientAsync(factory);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/fallback", content: null);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task CancellingAWaitingInteractionMarksTheJobCancelled()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);
        using var admin = await CreateAdminClientAsync(factory);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/cancel", content: null);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await database.ProviderAcquisitionJobs.SingleAsync(j => j.Id == jobId);
        Assert.AreEqual(ProviderAcquisitionJobLifecycleState.Cancelled, job.LifecycleState);
    }

    [TestMethod]
    public async Task StartingAnUnknownJobReturnsNotFound()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        using var admin = await CreateAdminClientAsync(factory);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{Guid.NewGuid()}/start", content: null);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task StartingAnExpiredInteractionReturnsConflict()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true, expiresInMinutes: -1);
        using var admin = await CreateAdminClientAsync(factory);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task AProviderThatDoesNotSupportInteractionControlCannotBeStarted()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: false);
        using var admin = await CreateAdminClientAsync(factory);
        var token = await WebTestFixture.GetAntiforgeryTokenAsync(admin);
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, token);

        var response = await admin.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task TwoAdminsRacingStartOneWinsAndTheOtherGetsAConflictNamingTheWinner()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);
        using var adminA = await CreateAdminClientAsync(factory);
        var tokenA = await WebTestFixture.GetAntiforgeryTokenAsync(adminA);
        adminA.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenA);

        var emailB = await CreateSecondAdminAsync(factory);
        using var adminB = await CreateSignedInClientAsync(factory, emailB, WebTestFixture.UserPassword);
        var tokenB = await WebTestFixture.GetAntiforgeryTokenAsync(adminB);
        adminB.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenB);

        var firstResponse = await adminA.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, firstResponse.StatusCode);

        var secondResponse = await adminB.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);
        Assert.AreEqual(HttpStatusCode.Conflict, secondResponse.StatusCode);

        var body = await secondResponse.Content.ReadFromJsonAsync<ConflictBody>();
        Assert.IsNotNull(body);
        StringAssert.Contains(body.message, "Take over");
        // Admin A's bootstrap display name is derived from the local part of
        // its email (see IdentityInitializer): "admin@..." -> "admin".
        Assert.AreEqual("admin", body.claimedBy);
    }

    [TestMethod]
    public async Task TakeOverReplacesAnExistingClaimAndLetsTheNewAdminStart()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);
        using var adminA = await CreateAdminClientAsync(factory);
        var tokenA = await WebTestFixture.GetAntiforgeryTokenAsync(adminA);
        adminA.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenA);

        var emailB = await CreateSecondAdminAsync(factory);
        using var adminB = await CreateSignedInClientAsync(factory, emailB, WebTestFixture.UserPassword);
        var tokenB = await WebTestFixture.GetAntiforgeryTokenAsync(adminB);
        adminB.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenB);

        var claimed = await adminA.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, claimed.StatusCode);

        var sessions = factory.Services.GetRequiredService<RemoteViewSessionRegistry>();
        Assert.IsTrue(sessions.TryAcquire(jobId, out var oldViewCloseSignal));
        var oldViewEnded = Task.Run(() =>
        {
            var closeRequested = oldViewCloseSignal.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            sessions.Release(jobId);
            return closeRequested;
        });

        var takeOver = await adminB.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/take-over", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, takeOver.StatusCode);
        Assert.IsTrue(await oldViewEnded, "Take over must close the old viewer before admitting the new holder.");

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var claim = await database.ProviderInteractionClaims.SingleAsync(c => c.JobId == jobId);
        var adminBUser = await database.Users.SingleAsync(user => user.Email == emailB);
        Assert.AreEqual(adminBUser.Id, claim.ClaimedByUserId);
    }

    [TestMethod]
    public async Task ALapsedClaimCanBeReclaimedByAnotherAdmin()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await database.ProviderAcquisitionJobs.SingleAsync(j => j.Id == jobId);
            var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);
            // Older than any realistic ClaimLeaseSeconds and with no active
            // viewer -- IsLeaseLive must report this claim as lapsed.
            var staleClaim = new ProviderInteractionClaim(
                jobId, job.ExternalProviderId, admin.Id, ProviderInteractionClaimChannel.InApp, null,
                DateTimeOffset.UtcNow.AddHours(-1));
            database.ProviderInteractionClaims.Add(staleClaim);
            await database.SaveChangesAsync();
        }

        var emailB = await CreateSecondAdminAsync(factory);
        using var adminB = await CreateSignedInClientAsync(factory, emailB, WebTestFixture.UserPassword);
        var tokenB = await WebTestFixture.GetAntiforgeryTokenAsync(adminB);
        adminB.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenB);

        var response = await adminB.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task CancelSucceedsForAnyAdminEvenWhenAnotherAdminHoldsTheClaim()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        var jobId = await SeedWaitingJobAsync(factory, supportsInteractionControl: true);
        using var adminA = await CreateAdminClientAsync(factory);
        var tokenA = await WebTestFixture.GetAntiforgeryTokenAsync(adminA);
        adminA.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenA);

        var claimed = await adminA.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/start", content: null);
        Assert.AreEqual(HttpStatusCode.NoContent, claimed.StatusCode);

        var emailB = await CreateSecondAdminAsync(factory);
        using var adminB = await CreateSignedInClientAsync(factory, emailB, WebTestFixture.UserPassword);
        var tokenB = await WebTestFixture.GetAntiforgeryTokenAsync(adminB);
        adminB.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, tokenB);

        var response = await adminB.PostAsync(
            $"/api/v1/admin/requests/provider-interactions/{jobId}/cancel", content: null);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    public async Task AnOrdinaryUserCannotReachTheInteractionQueue()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services => ReplaceProviderClient(services, new FakeInteractionControlClient()));

        using var reader = await CreateUserClientAsync(factory);
        var response = await reader.GetAsync("/api/v1/admin/requests/provider-interactions");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static void ReplaceProviderClient(IServiceCollection services, IExternalProviderClient client)
    {
        services.RemoveAll<IExternalProviderClient>();
        services.AddSingleton<IExternalProviderClient>(client);
    }

    /// <summary>Seeds a second, distinct admin account (the bootstrap admin already exists) for claim-racing tests.</summary>
    private static async Task<string> CreateSecondAdminAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"second-admin-{Guid.NewGuid():N}@family-librarian.example";
        var user = new AppUser { UserName = email, Email = email, DisplayName = "Second Admin", EmailConfirmed = true };
        Assert.IsTrue((await users.CreateAsync(user, WebTestFixture.UserPassword)).Succeeded);
        Assert.IsTrue((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        return email;
    }

    private sealed record ConflictBody(string message, string? claimedBy);

    /// <summary>
    /// Signed-in HTTP clients from this fixture's own default-configured
    /// factory would bypass every override this file registers on its own,
    /// per-test <see cref="FamilyLibrarianAppFactory"/> -- each test must
    /// sign in against its own local <paramref name="factory"/> instead.
    /// </summary>
    private static Task<HttpClient> CreateAdminClientAsync(FamilyLibrarianAppFactory factory) =>
        CreateSignedInClientAsync(factory, FamilyLibrarianAppFactory.AdminEmail, FamilyLibrarianAppFactory.AdminPassword);

    private static Task<HttpClient> CreateUserClientAsync(FamilyLibrarianAppFactory factory) =>
        CreateSignedInClientAsync(factory, WebTestFixture.UserEmail, WebTestFixture.UserPassword);

    private static async Task<HttpClient> CreateSignedInClientAsync(
        FamilyLibrarianAppFactory factory, string email, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, $"Test setup could not sign in as {email}.");
        return client;
    }

    private static async Task<Guid> SeedWaitingJobAsync(
        FamilyLibrarianAppFactory factory, bool supportsInteractionControl, int expiresInMinutes = 10)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);

        var work = new Work("A Provider Interaction Test Book", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);

        var request = new BookRequest(admin.Id, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        var format = request.Formats.Single();

        var providerId = $"interaction-test-provider-{Guid.NewGuid():N}";
        var provider = new ExternalProvider(providerId, "Interaction Test Provider", "http://interaction-test-provider.invalid", now);
        var features = supportsInteractionControl
            ? "waiting-interaction-control,interaction-view"
            : "";
        provider.RecordTestResult(
            succeeded: true, message: null, protocolVersion: "2",
            capabilities: $"mediaTypes:ebook;operations:search,acquire;features:{features}",
            actorUserId: null, testedAtUtc: now, manifestReached: true);
        database.ExternalProviders.Add(provider);

        var job = new ProviderAcquisitionJob(
            request.Id, format.Id, provider.Id, provider.ProviderId, null,
            Guid.NewGuid().ToString("N"), "candidate-ref", null, null, now);
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, now, now);
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
            interactionType: "browser", interactionMessage: "solve the check",
            interactionExpiresAtUtc: now.AddMinutes(expiresInMinutes), interactionResumeSupported: true,
            interactionActionUrl: null, progressPercent: null, progressBytesCompleted: null,
            progressBytesTotal: null, progressMessage: null, nextPollAtUtc: now.AddSeconds(30), atUtc: now);

        database.ProviderAcquisitionJobs.Add(job);
        await database.SaveChangesAsync();
        return job.Id;
    }

    /// <summary>
    /// Answers start/fallback with "still waiting" and cancel with a no-op,
    /// matching a real provider's idempotent posture. Every method this test
    /// class never calls throws, the same default-safe posture
    /// <c>AlwaysEmptyExternalProviderClient</c> uses (not reused directly:
    /// that type is sealed, and only two of its methods need different
    /// behavior here).
    /// </summary>
    private sealed class FakeInteractionControlClient : IExternalProviderClient
    {
        public Task<ExternalProviderManifest> GetManifestAsync(
            string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderHealth> GetHealthAsync(
            string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
            string baseUrl, string? apiKey, ExternalProviderSearchRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> AcquireAsync(
            string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
            string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderArtifact> GetOutputAsync(
            string baseUrl, string? apiKey, string jobId, string outputId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAcquireAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderJobStatus> StartInteractionAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
                new ProviderInteraction("browser", "still solving the check", DateTimeOffset.UtcNow.AddMinutes(10), true, null),
                null, null, 5));

        public Task<ExternalProviderJobStatus> UseAcquireFallbackAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Running, "fallback", null, null, null, 2));

        public Task CancelAcquireAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
