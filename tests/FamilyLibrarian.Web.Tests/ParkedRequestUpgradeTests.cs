using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A6-UPGRADE-1 U-1, end to end. A request parked in review on a held copy keeps looking; a better copy that passes its checks
/// withdraws the review, tells administrators and replaces the held copy; anything weaker, or a better copy that fails, changes nothing.
/// Each scenario has its own database because the recheck loop considers every request and provider in the database.
/// </summary>
[TestClass]
public sealed class ParkedRequestUpgradeEndToEndTests
{
    internal sealed class MutableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    internal sealed record Harnessed(FamilyLibrarianAppFactory Factory, UpgradeProviderClient Provider, MutableClock Clock, Guid RequestId, Guid FormatId, HttpClient Admin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await Factory.DisposeAsync();
        }

        public async Task RoundAsync()
        {
            foreach (var step in new Func<IServiceProvider, Task>[]
            {
                sp => sp.GetRequiredService<ExternalProviderRecheckService>().ProcessDueAsync(CancellationToken.None),
                sp => sp.GetRequiredService<AcquisitionJobPollingService>().ProcessDueAsync(CancellationToken.None),
                sp => sp.GetRequiredService<AutomaticRequestFulfillmentService>().ProcessPendingAsync(CancellationToken.None),
                sp => sp.GetRequiredService<AssetDiscardService>().DiscardSupersededHeldCopiesAsync(50, CancellationToken.None)
            })
            {
                await using var scope = Factory.Services.CreateAsyncScope();
                await step(scope.ServiceProvider);
            }
        }

        public async Task<(RequestStatus Status, IReadOnlyList<MediaAsset> Assets)> StateAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var status = (await db.BookRequests.AsNoTracking().SingleAsync(r => r.Id == RequestId)).Status;
            var assets = await db.MediaAssets.AsNoTracking().Where(a => a.AssociatedRequestFormatId == FormatId).OrderBy(a => a.CreatedAtUtc).ToListAsync();
            return (status, assets);
        }

        public async Task<int> BetterCopyNotificationsAsync()
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationEvents
                .CountAsync(e => e.Category == NotificationCategories.BetterCopyFound && e.SubjectId == RequestId.ToString());
        }
    }

    /// <summary>Starts with a MOBI copy the provider offers first: it has no content check, so it is held and the request ends up in review.</summary>
    private static async Task<Harnessed> ParkedOnAHeldCopyAsync()
    {
        var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var provider = new UpgradeProviderClient { CandidateId = "cand-1", OutputFormat = "mobi" };
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IExternalProviderClient>();
            services.AddSingleton<IExternalProviderClient>(provider);
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await admin.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest
        { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword })).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var create = await admin.PostAsJsonAsync("/api/v1/admin/external-providers/", new CreateExternalProviderRequest("upgrade-external", "Upgrade Source", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();
        var registered = (await create.Content.ReadFromJsonAsync<ExternalProviderResponse>())!;
        (await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{registered.Id}/enabled", new SetExternalProviderEnabledRequest(true))).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{registered.Id}/recheck-schedule", new SetExternalProviderRecheckScheduleRequest("Daily"))).EnsureSuccessStatusCode();
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        var harness = new Harnessed(factory, provider, clock, request.Id, request.Formats.Single().FormatId, admin);

        for (var round = 0; round < 6; round++)
        {
            await harness.RoundAsync();
            clock.Advance(TimeSpan.FromDays(30));
            var (status, assets) = await harness.StateAsync();
            if (status == RequestStatus.NeedsReview && assets.Any(a => a.StorageState == MediaAssetStorageState.Unmatched)) break;
        }

        return harness;
    }

    private static async Task RoundsAsync(Harnessed h, int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            await h.RoundAsync();
            h.Clock.Advance(TimeSpan.FromDays(30));
        }
    }

    [TestMethod]
    public async Task ABetterCopyFoundLaterReplacesTheHeldCopyWithdrawsTheReviewAndTellsAdministrators()
    {
        await using var h = await ParkedOnAHeldCopyAsync();
        var (parkedStatus, parkedAssets) = await h.StateAsync();
        Assert.AreEqual(RequestStatus.NeedsReview, parkedStatus, "Precondition: the request is parked in review.");
        var held = parkedAssets.Single(a => a.StorageState == MediaAssetStorageState.Unmatched);
        var submitsBefore = h.Provider.SubmitCalls;

        h.Provider.CandidateId = "cand-2";
        h.Provider.OutputFormat = "epub";
        await RoundsAsync(h, 3);

        Assert.IsGreaterThan(submitsBefore, h.Provider.SubmitCalls, "A month later the recheck looked again and fetched the better copy.");
        var (status, assets) = await h.StateAsync();
        Assert.AreNotEqual(RequestStatus.NeedsReview, status, "The review is withdrawn once the better copy passed.");
        Assert.IsTrue(assets.Any(a => a.StorageState is MediaAssetStorageState.Trusted or MediaAssetStorageState.Archived), "The better copy is trusted.");
        Assert.AreEqual(MediaAssetStorageState.Destroyed, assets.Single(a => a.Id == held.Id).StorageState, "The held copy it replaces is removed promptly.");
        Assert.AreEqual(1, await h.BetterCopyNotificationsAsync(), "Administrators are told once.");
    }

    [TestMethod]
    public async Task WithNothingBetterTheRequestStaysParkedTheHeldCopyStaysAndNothingIsFetchedAgain()
    {
        await using var h = await ParkedOnAHeldCopyAsync();
        var (_, parkedAssets) = await h.StateAsync();
        var held = parkedAssets.Single(a => a.StorageState == MediaAssetStorageState.Unmatched);
        var submitsBefore = h.Provider.SubmitCalls;

        await RoundsAsync(h, 3);

        Assert.AreEqual(submitsBefore, h.Provider.SubmitCalls, "The same candidate is never fetched again.");
        var (status, assets) = await h.StateAsync();
        Assert.AreEqual(RequestStatus.NeedsReview, status);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, assets.Single(a => a.Id == held.Id).StorageState, "Nothing replaced it, so it is kept for a person.");
        Assert.AreEqual(0, await h.BetterCopyNotificationsAsync());
    }

    [TestMethod]
    public async Task AWeakerCandidateIsNeverFetchedWhileARequestIsParked()
    {
        await using var h = await ParkedOnAHeldCopyAsync();
        var (_, parkedAssets) = await h.StateAsync();
        var held = parkedAssets.Single(a => a.StorageState == MediaAssetStorageState.Unmatched);
        var submitsBefore = h.Provider.SubmitCalls;

        h.Provider.CandidateId = "cand-2";
        h.Provider.OutputFormat = "epub";
        h.Provider.StatesIdentifiers = false;
        // Make the source liberal enough to accept this candidate for an ordinary request, so the only thing holding it back is
        // that the request is parked.
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var providerRowId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ExternalProviders.AsNoTracking()
                .Where(p => p.ProviderId == "upgrade-external").Select(p => p.Id).SingleAsync();
            (await h.Admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{providerRowId}/confidence-level",
                new { level = "Liberal", ebook = "Liberal", audiobook = "Liberal" })).EnsureSuccessStatusCode();
        }

        await RoundsAsync(h, 3);

        Assert.AreEqual(submitsBefore, h.Provider.SubmitCalls, "Only a strong candidate is worth a download while the request is parked.");
        var (status, assets) = await h.StateAsync();
        Assert.AreEqual(RequestStatus.NeedsReview, status);
        Assert.AreEqual(MediaAssetStorageState.Unmatched, assets.Single(a => a.Id == held.Id).StorageState);
    }

    [TestMethod]
    public async Task ABetterLookingCopyThatFailsItsChecksChangesNothing()
    {
        await using var h = await ParkedOnAHeldCopyAsync();
        var (_, parkedAssets) = await h.StateAsync();
        var held = parkedAssets.Single(a => a.StorageState == MediaAssetStorageState.Unmatched);

        h.Provider.CandidateId = "cand-2";
        h.Provider.OutputFormat = "epub";
        h.Provider.OutputTitle = "A Completely Different Book";
        await RoundsAsync(h, 3);

        var (status, assets) = await h.StateAsync();
        Assert.AreEqual(RequestStatus.NeedsReview, status, "The wrong book does not close the review.");
        Assert.AreEqual(MediaAssetStorageState.Unmatched, assets.Single(a => a.Id == held.Id).StorageState, "The held copy is not removed for a copy that failed.");
        Assert.IsFalse(assets.Any(a => a.StorageState is MediaAssetStorageState.Trusted or MediaAssetStorageState.Archived));
        Assert.AreEqual(0, await h.BetterCopyNotificationsAsync());
    }
}

internal sealed class UpgradeProviderClient : IExternalProviderClient
{
    public string CandidateId { get; set; } = "cand-1";

    public string OutputFormat { get; set; } = "epub";

    public string OutputTitle { get; set; } = "The Hobbit";

    /// <summary>When false the candidate states no identifiers and gives only the author's last name: a title-only style match (last name only), plausible but not strong.</summary>
    public bool StatesIdentifiers { get; set; } = true;

    public int SubmitCalls { get; private set; }

    private byte[] OutputBytes() => OutputFormat == "mobi" ? MobiTestFixture.Build() : EpubTestFixture.BuildMinimalEpubBytes(OutputTitle);

    private string OutputName => $"the-hobbit.{OutputFormat}";

    public Task<ExternalProviderManifest> GetManifestAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderManifest(["2"], "2", null, "upgrade-external", "Upgrade Source", "1.0.0",
            new ProviderCapabilities(["ebook"], ["search", "acquire"], []), null, null, null));

    public Task<ExternalProviderHealth> GetHealthAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderHealth(ProviderHealthStatus.Healthy, ProviderOperationalStatus.Available, ProviderOperationalStatus.Available));

    public Task<IReadOnlyList<ExternalProviderCandidate>> SearchAsync(
        string baseUrl, string? apiKey, ExternalProviderSearchRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<ExternalProviderCandidate> candidates = request.MediaType == RequestMediaType.Ebook
            ?
            [
                new ExternalProviderCandidate(CandidateId,
                    new ExternalProviderWorkEvidence("The Hobbit", null, [new BookAuthor(StatesIdentifiers ? "J. R. R. Tolkien" : "Tolkien", "author")], [], []),
                    new ExternalProviderEditionEvidence("en", null, null, StatesIdentifiers ? request.Edition?.Identifiers ?? [] : []),
                    new ExternalProviderReleaseEvidence(null, OutputFormat, null, false, 1, false, null, null, [], null, ExternalProviderDrmStatus.None))
            ]
            : [];
        return Task.FromResult(candidates);
    }

    public Task<ExternalProviderArtifact> AcquireAsync(string baseUrl, string? apiKey, string candidateReference, RequestMediaType mediaType, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(new MemoryStream(OutputBytes()), OutputName));

    public Task<ExternalProviderAcquireSubmission> SubmitAcquireAsync(
        string baseUrl, string? apiKey, ExternalAcquireRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        SubmitCalls++;
        return Task.FromResult(ExternalProviderAcquireSubmission.Accepted("fake-job-" + SubmitCalls, ProviderAcquisitionJobLifecycleState.Completed, phase: null, pollAfterSeconds: 0));
    }

    public Task<ExternalProviderJobStatus> GetAcquireStatusAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderJobStatus(jobId, ProviderAcquisitionJobLifecycleState.Completed, Phase: null, Interaction: null, Progress: null, Error: null, PollAfterSeconds: null));

    public Task<IReadOnlyList<ExternalProviderOutput>> ListOutputsAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ExternalProviderOutput>>(
        [
            new ExternalProviderOutput("primary", ProviderOutputKind.File, "primary", OutputName,
                OutputFormat == "mobi" ? "application/x-mobipocket-ebook" : "application/epub+zip", null, null, null, null, null)
        ]);

    public Task<ExternalProviderArtifact> GetOutputAsync(string baseUrl, string? apiKey, string jobId, string outputId, CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalProviderArtifact(new MemoryStream(OutputBytes()), OutputName));

    public Task CancelAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAcquireAsync(string baseUrl, string? apiKey, string jobId, CancellationToken cancellationToken) => Task.CompletedTask;
}
