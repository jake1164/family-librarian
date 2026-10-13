using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

// AUTOFIND Phase 1. A scheduled recheck stores candidates for the requester to choose between only when
// they could be this book. Results that contradict the requested work, and editions only in another
// language, are recorded as "no acceptable copy": the request stays in the automatic queue, no review
// is stored and nobody is notified. Each scenario is its own class with its own database for the same
// reason as ExternalProviderAutomaticAcquisitionEndpointTests (the recheck considers every provider).

internal sealed record NoAcceptableCopyOutcome(
    BookRequestResponse Request, ProviderAttemptResponse Attempt, RequestStatus Status,
    int StoredReviewCandidates, int ReviewNotifications, AdminProviderDebugCandidateResponse[] Diagnostics);

internal static class NoAcceptableCopyScenario
{
    private const string ProviderId = "example-source";

    public static async Task<NoAcceptableCopyOutcome> RunAsync(
        WebTestFixture fixture, IReadOnlyList<ExternalProviderCandidate> results)
    {
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(new FixedResultsExternalProviderClient(results));
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, login.StatusCode);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var created = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest(ProviderId, "Example Source", "http://fake-external.test"));
        var provider = await created.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily"))).EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var createdRequest = await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = await createdRequest.Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var rechecks = scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>();
            Assert.IsTrue(await rechecks.ProcessDueAsync(CancellationToken.None) >= 1);
        }

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        var diagnostics = await admin.GetFromJsonAsync<AdminProviderDebugCandidateResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/formats/{request.Formats.Single().FormatId}/providers/{ProviderId}/debug-search");
        Assert.IsNotNull(diagnostics);

        await using var verification = factory.Services.CreateAsyncScope();
        var database = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await database.BookRequests.SingleAsync(bookRequest => bookRequest.Id == request.Id);
        return new(
            request, attempts.Single(), persisted.Status,
            await database.RequestReviewCandidates.CountAsync(candidate => candidate.RequestId == request.Id),
            await database.NotificationEvents.CountAsync(item =>
                item.Category == NotificationCategories.RequestNeedsReview ||
                item.Category == NotificationCategories.RequestPreferenceAmbiguity),
            diagnostics);
    }

    public static ExternalProviderCandidate Result(
        string reference, string title, string author, string? language = "en") =>
        new(reference,
            new ExternalProviderWorkEvidence(title, null, [new BookAuthor(author, "author")], [], []),
            new ExternalProviderEditionEvidence(language, null, null, []),
            new ExternalProviderReleaseEvidence(null, "epub", 1_048_576, false, 1, false, null, null, [], null,
                ExternalProviderDrmStatus.None));
}

/// <summary>Fifteen results that share nothing with the requested book are "none of them is this book".</summary>
[TestClass]
public sealed class ExternalProviderUnrelatedResultsEndpointTests
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
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task UnrelatedResultsAreRecordedAsNoMatchWithoutAReviewOrANotification()
    {
        var results = Enumerable.Range(1, 15).Select(index => NoAcceptableCopyScenario.Result(
            $"unrelated-{index}", $"Garden Birds of Europe, Volume {index}", "Someone Unrelated")).ToArray();

        var outcome = await NoAcceptableCopyScenario.RunAsync(WebTestFixture.Require(_fixture), results);

        Assert.AreEqual("NoMatch", outcome.Attempt.Outcome);
        Assert.AreEqual("Found 15 result(s); none of them is this book.", outcome.Attempt.Summary);
        Assert.IsNotNull(outcome.Attempt.NextEligibleCheckAtUtc, "The recheck stays scheduled.");
        Assert.AreEqual(RequestStatus.PendingAcquisition, outcome.Status);
        Assert.AreEqual(0, outcome.StoredReviewCandidates);
        Assert.AreEqual(0, outcome.ReviewNotifications);
        // The admin debug search is diagnostic: the same results stay visible there.
        Assert.HasCount(15, outcome.Diagnostics);
        Assert.IsTrue(outcome.Diagnostics.All(candidate => candidate.IdentityEvidence?.AcquisitionSuitability == "Blocked"));
    }
}

/// <summary>An edition in another language is not offered to the requester as a choice (Phase 5 owns asking).</summary>
[TestClass]
public sealed class ExternalProviderForeignLanguageOnlyEndpointTests
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
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task AnEditionOnlyInAnotherLanguageIsRecordedAsNoMatchWithTheLanguageNamed()
    {
        var outcome = await NoAcceptableCopyScenario.RunAsync(
            WebTestFixture.Require(_fixture),
            [NoAcceptableCopyScenario.Result("foreign-1", "The Hobbit", "J. R. R. Tolkien", language: "fr")]);

        Assert.AreEqual("NoMatch", outcome.Attempt.Outcome);
        Assert.AreEqual("Found only editions in other languages (French).", outcome.Attempt.Summary);
        Assert.IsNotNull(outcome.Attempt.NextEligibleCheckAtUtc);
        Assert.AreEqual(RequestStatus.PendingAcquisition, outcome.Status);
        Assert.AreEqual(0, outcome.StoredReviewCandidates);
        Assert.AreEqual(0, outcome.ReviewNotifications);
        Assert.HasCount(1, outcome.Diagnostics);
    }
}

/// <summary>A result that could be this book still reaches review when a blocked one is found beside it.</summary>
[TestClass]
public sealed class ExternalProviderBlockedAndPlausibleEndpointTests
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
        if (_fixture is not null) await _fixture.DisposeAsync();
    }

    [TestMethod]
    public async Task OnlyThePlausibleResultReachesReviewWhileTheBlockedOneStaysVisibleToAdministrators()
    {
        var outcome = await NoAcceptableCopyScenario.RunAsync(
            WebTestFixture.Require(_fixture),
            [
                NoAcceptableCopyScenario.Result("unrelated-1", "Garden Birds of Europe", "Someone Unrelated"),
                NoAcceptableCopyScenario.Result("plausible-1", "The Hobbit", "J. R. R. Tolkien"),
            ]);

        Assert.AreEqual("CandidatesFound", outcome.Attempt.Outcome);
        Assert.AreEqual(RequestStatus.NeedsReview, outcome.Status);
        Assert.AreEqual(1, outcome.StoredReviewCandidates);
        Assert.HasCount(2, outcome.Diagnostics);
        Assert.IsTrue(outcome.Diagnostics.Any(candidate => candidate.ProviderResultId == "unrelated-1"));
    }
}

file sealed class FixedResultsExternalProviderClient(IReadOnlyList<ExternalProviderCandidate> results) : IExternalProviderClient
{
    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "example-source", "Example Source", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(request.MediaType == RequestMediaType.Ebook ? results : []);

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No acceptable copy must never be downloaded.");

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No acceptable copy must never be downloaded.");

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CancelAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
