using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Authentication;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Infrastructure.Identity;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Identity;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// HUMAN-ACQ-1 WP5/WP6: the anonymous magic-link claim endpoint and the
/// grant-protected job routes it signs the caller into.
/// </summary>
[TestClass]
public sealed class InteractionLinkEndpointTests
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
    public async Task ClaimingWithAnUnknownTokenReturnsNotFound()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/interaction-links/claim", new ClaimInteractionLinkRequest("not-a-real-token"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ClaimingWithAnEmptyTokenIsRejected()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/interaction-links/claim", new ClaimInteractionLinkRequest(string.Empty));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task ClaimingAValidTokenSignsInAGrantScopedToThatOneJob()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString, services => ReplaceProviderClient(services, new WorkingInteractionControlClient()));
        using var client = factory.CreateClient();

        var (token, jobId) = await SeedClaimableAlertAsync(factory);

        var claimResponse = await client.PostAsJsonAsync(
            "/api/v1/interaction-links/claim", new ClaimInteractionLinkRequest(token));
        Assert.AreEqual(HttpStatusCode.OK, claimResponse.StatusCode);
        var body = await claimResponse.Content.ReadFromJsonAsync<ClaimInteractionLinkResponse>();
        Assert.IsNotNull(body);
        Assert.AreEqual(jobId, body.JobId);

        // The grant cookie now on this client reaches this job's status route.
        var statusResponse = await client.GetAsync($"/api/v1/interaction-links/jobs/{jobId}/status");
        Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
    }

    [TestMethod]
    public async Task DisablingTheRecipientRevokesAnExistingInteractionGrant()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString, services => ReplaceProviderClient(services, new WorkingInteractionControlClient()));
        using var client = factory.CreateClient();

        Guid adminId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var email = $"grant-revocation-{Guid.NewGuid():N}@family-librarian.example";
            var admin = new AppUser { UserName = email, Email = email, DisplayName = "grant recipient", EmailConfirmed = true };
            Assert.IsTrue((await users.CreateAsync(admin, WebTestFixture.UserPassword)).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(admin, "Admin")).Succeeded);
            adminId = admin.Id;
        }

        var (token, jobId) = await SeedClaimableAlertAsync(factory, adminId);
        var claim = await client.PostAsJsonAsync("/api/v1/interaction-links/claim", new ClaimInteractionLinkRequest(token));
        Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await client.GetAsync($"/api/v1/interaction-links/jobs/{jobId}/status")).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await database.Users.SingleAsync(user => user.Id == adminId);
            admin.Status = UserStatus.Disabled;
            await database.SaveChangesAsync();
        }

        var status = await client.GetFromJsonAsync<InteractionLinkStatusResponse>(
            $"/api/v1/interaction-links/jobs/{jobId}/status");
        Assert.AreEqual("released", status?.State);
    }

    [TestMethod]
    public async Task AGrantForOneJobCannotReachAnotherJobsStatus()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString, services => ReplaceProviderClient(services, new WorkingInteractionControlClient()));
        using var client = factory.CreateClient();

        var (token, _) = await SeedClaimableAlertAsync(factory);
        var otherJobId = await SeedUnrelatedWaitingJobAsync(factory);

        var claimResponse = await client.PostAsJsonAsync(
            "/api/v1/interaction-links/claim", new ClaimInteractionLinkRequest(token));
        Assert.AreEqual(HttpStatusCode.OK, claimResponse.StatusCode);

        var response = await client.GetAsync($"/api/v1/interaction-links/jobs/{otherJobId}/status");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task AnOrdinaryAdminCookieCannotReachTheGrantProtectedStatusRoute()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString);

        var (_, jobId) = await SeedClaimableAlertAsync(factory);

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginResponse = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword });
        Assert.AreEqual(HttpStatusCode.NoContent, loginResponse.StatusCode);

        var response = await admin.GetAsync($"/api/v1/interaction-links/jobs/{jobId}/status");

        // The admin cookie authenticates under a different scheme than
        // "InteractionGrant" -- unrecognized here, so this is anonymous, not
        // merely unauthorized-by-role.
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task AnInteractionGrantCookieCannotReachTheAdminRequestsQueue()
    {
        var fixture = WebTestFixture.Require(_fixture);
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString, services => ReplaceProviderClient(services, new WorkingInteractionControlClient()));
        using var client = factory.CreateClient();

        var (token, _) = await SeedClaimableAlertAsync(factory);
        var claimResponse = await client.PostAsJsonAsync(
            "/api/v1/interaction-links/claim", new ClaimInteractionLinkRequest(token));
        Assert.AreEqual(HttpStatusCode.OK, claimResponse.StatusCode);

        var response = await client.GetAsync("/api/v1/admin/requests/");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Seeds a waiting job, an Open alert, and one recipient whose token hashes to a known plaintext value.</summary>
    private static async Task<(string Token, Guid JobId)> SeedClaimableAlertAsync(
        FamilyLibrarianAppFactory factory, Guid? recipientUserId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenGenerator = scope.ServiceProvider.GetRequiredService<ISecureTokenGenerator>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);

        var work = new Work("An Interaction Link Test Book", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);
        var request = new BookRequest(admin.Id, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        var format = request.Formats.Single();

        var providerId = $"interaction-link-test-provider-{Guid.NewGuid():N}";
        var provider = new ExternalProvider(providerId, "Interaction Link Test Provider", "http://interaction-link-test-provider.invalid", now);
        provider.RecordTestResult(
            succeeded: true, message: null, protocolVersion: "2",
            capabilities: "mediaTypes:ebook;operations:search,acquire;features:waiting-interaction-control,interaction-view",
            actorUserId: null, testedAtUtc: now, manifestReached: true);
        database.ExternalProviders.Add(provider);

        var job = new ProviderAcquisitionJob(
            request.Id, format.Id, provider.Id, provider.ProviderId, null,
            Guid.NewGuid().ToString("N"), "candidate-ref", null, null, now);
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, now, now);
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
            interactionType: "browser", interactionMessage: "solve the check",
            interactionExpiresAtUtc: now.AddMinutes(10), interactionResumeSupported: true,
            interactionActionUrl: null, progressPercent: null, progressBytesCompleted: null,
            progressBytesTotal: null, progressMessage: null, nextPollAtUtc: now.AddSeconds(30), atUtc: now);
        database.ProviderAcquisitionJobs.Add(job);

        var alert = new ProviderInteractionAlert(provider.Id, provider.ProviderId, provider.DisplayName, now);
        var recipient = alert.AddRecipient(recipientUserId ?? admin.Id);
        var token = tokenGenerator.CreateToken();
        recipient.RecordSendSuccess(tokenGenerator.Hash(token), "!room:example.test", "$event1", now);
        database.ProviderInteractionAlerts.Add(alert);

        await database.SaveChangesAsync();
        return (token, job.Id);
    }

    private static async Task<Guid> SeedUnrelatedWaitingJobAsync(FamilyLibrarianAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var admin = await database.Users.SingleAsync(user => user.Email == FamilyLibrarianAppFactory.AdminEmail);
        var work = new Work("An Unrelated Book", null, null, null, PublicationStatus.Published, now);
        database.Works.Add(work);
        var request = new BookRequest(admin.Id, work.Id, [RequestMediaType.Ebook], null, now);
        database.BookRequests.Add(request);
        var format = request.Formats.Single();

        var providerId = $"unrelated-provider-{Guid.NewGuid():N}";
        var provider = new ExternalProvider(providerId, "Unrelated Provider", "http://unrelated-provider.invalid", now);
        database.ExternalProviders.Add(provider);

        var job = new ProviderAcquisitionJob(
            request.Id, format.Id, provider.Id, provider.ProviderId, null,
            Guid.NewGuid().ToString("N"), "candidate-ref", null, null, now);
        job.RecordSubmission("provider-job-1", ProviderAcquisitionJobLifecycleState.Waiting, now, now);
        job.ApplyStatus(
            ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
            interactionType: "browser", interactionMessage: "solve the check",
            interactionExpiresAtUtc: now.AddMinutes(10), interactionResumeSupported: true,
            interactionActionUrl: null, progressPercent: null, progressBytesCompleted: null,
            progressBytesTotal: null, progressMessage: null, nextPollAtUtc: now.AddSeconds(30), atUtc: now);
        database.ProviderAcquisitionJobs.Add(job);

        await database.SaveChangesAsync();
        return job.Id;
    }

    private static void ReplaceProviderClient(IServiceCollection services, IExternalProviderClient client)
    {
        services.RemoveAll<IExternalProviderClient>();
        services.AddSingleton(client);
    }

    /// <summary>
    /// Answers <c>interaction/start</c> with success so a claim's own provider-start
    /// step reaches <see cref="ProviderInteractionCommandResult.Success"/> instead of
    /// the default test double's <see cref="NotSupportedException"/> (which the claim
    /// path deliberately treats as a kept-but-pending claim, 503 -- exercised by
    /// letting the default double stand, not needed for these grant/cookie-focused tests).
    /// </summary>
    private sealed class WorkingInteractionControlClient : IExternalProviderClient
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
            string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExternalProviderJobStatus> StartInteractionAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            Task.FromResult(new ExternalProviderJobStatus(
                jobId, ProviderAcquisitionJobLifecycleState.Waiting, "user-interaction",
                new ProviderInteraction("browser", "still solving the check", DateTimeOffset.UtcNow.AddMinutes(10), true, null),
                null, null, 5));

        public Task<ExternalProviderJobStatus> UseAcquireFallbackAsync(
            string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CancelAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
