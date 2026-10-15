using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

// AUTOFIND 2a. A scheduled recheck pools several one-dimension search variants (other ISBNs, alternate titles,
// series + position, author spellings) and verifies every result against the requested work, never the variant.
// The demo Hobbit work has two ISBNs, a series position and a "J. R. R." author, so a pass plans four variants:
// base, other ISBN, "Middle-earth 0", "Tolkien, J. R. R.". One class per scenario, each with its own database,
// for the reason documented on ExternalProviderAutomaticAcquisitionEndpointTests.

internal sealed record VariantOutcome(
    ProviderAttemptResponse[] Attempts, VariantSearchProviderClient Client, Guid RequestId, ManualImportResult? Direct = null);

internal static class VariantScenario
{
    public static async Task<VariantOutcome> RunAsync(
        WebTestFixture fixture, VariantSearchProviderClient client, string providerId, int passes = 1, string? directResultId = null,
        bool autoAcquire = true, Func<FamilyLibrarianAppFactory, BookRequestResponse, int, Task>? beforePass = null,
        Func<FamilyLibrarianAppFactory, HttpClient, BookRequestResponse, Task>? afterwards = null,
        string? providerConfidence = null)
    {
        await using var factory = new FamilyLibrarianAppFactory(
            fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IExternalProviderClient>();
                services.AddSingleton<IExternalProviderClient>(client);
            });

        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await admin.PostAsJsonAsync(
            "/api/auth/login",
            new FamilyLibrarian.Contracts.Authentication.LoginRequest
            {
                Email = FamilyLibrarianAppFactory.AdminEmail,
                Password = FamilyLibrarianAppFactory.AdminPassword
            });
        Assert.AreEqual(HttpStatusCode.NoContent, login.StatusCode);
        admin.DefaultRequestHeaders.Add(
            AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));

        var created = await admin.PostAsJsonAsync(
            "/api/v1/admin/external-providers/",
            new CreateExternalProviderRequest(providerId, "Variant Source", "http://fake-variant.test"));
        var provider = await created.Content.ReadFromJsonAsync<ExternalProviderResponse>();
        Assert.IsNotNull(provider);
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/enabled", new SetExternalProviderEnabledRequest(true)))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest("Daily"))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync(
            $"/api/v1/admin/external-providers/{provider.Id}/auto-acquire",
            new SetExternalProviderAutoAcquireEnabledRequest(autoAcquire))).EnsureSuccessStatusCode();

        if (providerConfidence is not null)
            (await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{provider.Id}/confidence-level",
                new SetExternalProviderConfidenceLevelRequest(providerConfidence, null, null))).EnsureSuccessStatusCode();

        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>();
        Assert.IsNotNull(work);
        var request = await (await admin.PostAsJsonAsync(
            "/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false)))
            .Content.ReadFromJsonAsync<BookRequestResponse>();
        Assert.IsNotNull(request);

        ManualImportResult? direct = null;
        if (directResultId is not null)
        {
            // No recheck: the caller hands the security service a candidate id with no variant key, as a review pick did before 2b.
            await using var directScope = factory.Services.CreateAsyncScope();
            (direct, _) = await directScope.ServiceProvider.GetRequiredService<DirectAcquisitionSecurityService>()
                .AcquireEvaluateAndVerifyAsync(
                    request.Id, request.Formats.Single().FormatId, providerId, directResultId, CancellationToken.None);
        }

        for (var pass = 0; pass < (directResultId is null ? passes : 0); pass++)
        {
            if (beforePass is not null) await beforePass(factory, request, pass);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ExternalProviderRecheckService>()
                .ProcessDueAsync(CancellationToken.None);
        }

        var attempts = await admin.GetFromJsonAsync<ProviderAttemptResponse[]>(
            $"/api/v1/admin/requests/{request.Id}/provider-attempts");
        Assert.IsNotNull(attempts);
        if (afterwards is not null) await afterwards(factory, admin, request);
        return new(attempts, client, request.Id, direct);
    }
}

[TestClass]
public sealed class ExternalProviderSeriesVariantFindsTheBookEndpointTests
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
    public async Task AStrongResultFromALaterVariantIsAcquiredAndStopsTheList()
    {
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0"
                ? [VariantSearchProviderClient.Hobbit("series-hit", withAuthor: true)]
                : []);

        var outcome = await VariantScenario.RunAsync(WebTestFixture.Require(_fixture), client, "variant-series-source");

        Assert.AreEqual("Submitted", outcome.Attempts.Single().Outcome);
        Assert.AreEqual("series-hit", client.SubmittedCandidates.Single());
        // Three searches find it (base, other ISBN, series + position; the strong result ends the list before the author
        // spellings), then acquiring re-derives it by re-running exactly the series search: four searches in all.
        CollectionAssert.AreEqual(new List<string> { "The Hobbit", "The Hobbit", "Middle-earth 0", "Middle-earth 0" }, client.SearchedTitles);
    }
}

[TestClass]
public sealed class ExternalProviderPooledVariantsEndpointTests
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
    public async Task TheIdentifierCopyFromALaterVariantOutranksTheFirstFoundTitleOnlyCopy()
    {
        // The base search yields only an exact title with no author evidence (not strong). The other-ISBN search
        // yields a copy that carries the ISBN. Pooling must fetch the identifier copy, not the first one found.
        var client = new VariantSearchProviderClient(request =>
            request.Edition?.Identifiers.Any(identifier => identifier.Value == "9780008627838") == true
                ? [VariantSearchProviderClient.Hobbit("isbn-hit", withAuthor: true, echoIdentifiers: request.Edition.Identifiers)]
                : request.Work.Title == "The Hobbit"
                    ? [VariantSearchProviderClient.Hobbit("title-only", withAuthor: false)]
                    : []);

        var outcome = await VariantScenario.RunAsync(WebTestFixture.Require(_fixture), client, "variant-pool-source");

        Assert.AreEqual("isbn-hit", client.SubmittedCandidates.Single());
        Assert.AreEqual("Submitted", outcome.Attempts.Single().Outcome);
    }
}

[TestClass]
public sealed class ExternalProviderSameReleaseUnderManyReferencesEndpointTests
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
    public async Task TheSameReleaseFoundByEveryVariantIsOneCandidateAndOneDownload()
    {
        // Each variant posts the same release under its own reference (stable per variant, as a real source's are).
        var client = new VariantSearchProviderClient(request =>
            [VariantSearchProviderClient.Hobbit(
                $"same-release-{request.Work.Title}-{string.Join(',', request.Edition?.Identifiers.Select(identifier => identifier.Value) ?? [])}-{(request.Work.Authors.Count > 0 ? request.Work.Authors[0].Name : null)}",
                withAuthor: false)]);

        var outcome = await VariantScenario.RunAsync(WebTestFixture.Require(_fixture), client, "variant-dedupe-source",
            providerConfidence: "Liberal");

        Assert.HasCount(1, client.SubmittedCandidates);
        Assert.AreEqual("Submitted", outcome.Attempts.Single().Outcome);
        Assert.IsGreaterThan(1, client.SearchedTitles.Count, "A title-only result is not strong, so the whole list runs.");
    }
}

[TestClass]
public sealed class ExternalProviderRateLimitedVariantEndpointTests
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
    public async Task ARateLimitedVariantStopsTheListAndSchedulesAtLeastFifteenMinutesOut()
    {
        var calls = 0;
        var client = new VariantSearchProviderClient(_ =>
            Interlocked.Increment(ref calls) == 2
                ? throw new HttpRequestException(
                    HttpRequestError.Unknown, "Too many requests", inner: null, statusCode: HttpStatusCode.TooManyRequests)
                : []);

        var before = DateTimeOffset.UtcNow;
        var outcome = await VariantScenario.RunAsync(WebTestFixture.Require(_fixture), client, "variant-limited-source");

        Assert.AreEqual(2, client.SearchedTitles.Count, "No variant after the rate-limited one may be searched.");
        var attempt = outcome.Attempts.Single();
        Assert.AreEqual("Failed", attempt.Outcome);
        Assert.Contains("slow down", attempt.Summary);
        Assert.IsNotNull(attempt.NextEligibleCheckAtUtc);
        Assert.IsGreaterThanOrEqualTo(before.AddMinutes(14), attempt.NextEligibleCheckAtUtc.Value);
    }
}

[TestClass]
public sealed class ExternalProviderVariantResultsVerifiedAgainstTheWorkEndpointTests
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
    public async Task AFrenchEditionFoundByAVariantIsNeverAcquired()
    {
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0"
                ? [VariantSearchProviderClient.Hobbit("french-hit", withAuthor: true, language: "fr")]
                : []);

        var outcome = await VariantScenario.RunAsync(WebTestFixture.Require(_fixture), client, "variant-language-source");

        Assert.IsEmpty(client.SubmittedCandidates);
        var attempt = outcome.Attempts.Single();
        Assert.AreEqual("NoMatch", attempt.Outcome);
        Assert.AreEqual("Found only editions in other languages (French).", attempt.Summary);
    }
}

/// <summary>
/// Option (b) with a fallback: a candidate with no variant key (a review pick stored before the key existed) is re-derived by
/// the base search and then by walking the planned variants in order, so a candidate only a variant can find is still found.
/// </summary>
[TestClass]
public sealed class ExternalProviderVariantFallbackWalkEndpointTests
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
    public async Task ACandidateWithNoKeyIsFoundByTheFallbackWalk()
    {
        var client = new VariantSearchProviderClient(request =>
            request.Work.Title == "Middle-earth 0" ? [VariantSearchProviderClient.Hobbit("series-hit", withAuthor: true)] : []);

        var outcome = await VariantScenario.RunAsync(
            WebTestFixture.Require(_fixture), client, "variant-walk-source", directResultId: "series-hit");

        Assert.AreEqual(ManualImportOutcome.AcquisitionInProgress, outcome.Direct!.Outcome, outcome.Direct.Error);
        Assert.AreEqual("series-hit", client.SubmittedCandidates.Single());
        // Base, other ISBN, then the series search that finds it: the walk stops at the first hit.
        CollectionAssert.AreEqual(new List<string> { "The Hobbit", "The Hobbit", "Middle-earth 0" }, client.SearchedTitles);
    }
}

/// <summary>A candidate no search can find is refused exactly as before, after the walk has tried every planned variant.</summary>
[TestClass]
public sealed class ExternalProviderVariantNotFoundAnywhereEndpointTests
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
    public async Task ACandidateNoVariantFindsIsRefusedAsNoLongerAvailable()
    {
        var client = new VariantSearchProviderClient(_ => []);

        var outcome = await VariantScenario.RunAsync(
            WebTestFixture.Require(_fixture), client, "variant-missing-source", directResultId: "ghost");

        Assert.AreEqual(ManualImportOutcome.Invalid, outcome.Direct!.Outcome);
        Assert.AreEqual("That option is no longer available.", outcome.Direct.Error);
        Assert.IsEmpty(client.SubmittedCandidates);
        // Base, other ISBN, series + position, then the one author spelling this work plans ("Tolkien, J. R. R.").
        CollectionAssert.AreEqual(
            new List<string> { "The Hobbit", "The Hobbit", "Middle-earth 0", "The Hobbit" }, client.SearchedTitles);
    }
}

internal sealed class VariantSearchProviderClient(
    Func<ExternalProviderSearchRequest, IReadOnlyList<ExternalProviderCandidate>> search) : IExternalProviderClient
{
    private readonly object _gate = new();

    public List<string> SearchedTitles { get; } = [];

    public List<string> SubmittedCandidates { get; } = [];

    public static ExternalProviderCandidate Hobbit(
        string reference, bool withAuthor, string language = "en", IReadOnlyList<BookIdentifier>? echoIdentifiers = null) =>
        new(reference,
            new ExternalProviderWorkEvidence(
                "The Hobbit", null, withAuthor ? [new BookAuthor("J. R. R. Tolkien", "author")] : [], [], []),
            new ExternalProviderEditionEvidence(language, null, null, echoIdentifiers ?? []),
            new ExternalProviderReleaseEvidence(
                "Tolkien - The Hobbit (epub)", "epub", 1_200_000, false, 1, false, null, null, [], null,
                ExternalProviderDrmStatus.None));

    public static ExternalProviderCandidate Unrelated(string reference) =>
        new(reference,
            new ExternalProviderWorkEvidence(
                "Garden Birds of Europe", null, [new BookAuthor("Someone Unrelated", "author")], [], []),
            new ExternalProviderEditionEvidence("en", null, null, []),
            new ExternalProviderReleaseEvidence(
                "Garden Birds of Europe (epub)", "epub", 900_000, false, 1, false, null, null, [], null,
                ExternalProviderDrmStatus.None));

    public Task<ExternalProviderManifest> GetManifestAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(
            ["2"], "2", null, "variant-source", "Variant Source", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null));

    public Task<ExternalProviderHealth> GetHealthAsync(
        string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(
            ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.MediaType != RequestMediaType.Ebook)
        {
            return Task.FromResult<IReadOnlyList<ExternalProviderCandidate>>([]);
        }

        lock (_gate)
        {
            SearchedTitles.Add(request.Work.Title);
        }

        return Task.FromResult(search(request));
    }

    public Task<ExternalProviderArtifact> AcquireAsync(
        string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SubmittedCandidates.Add(request.CandidateReference);
        }

        return Task.FromResult(ExternalProviderAcquireSubmission.Accepted(
            "variant-job", ProviderAcquisitionJobLifecycleState.Running, phase: null, pollAfterSeconds: 60));
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(
        string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ExternalProviderArtifact> GetOutputAsync(
        string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CancelAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task DeleteAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
