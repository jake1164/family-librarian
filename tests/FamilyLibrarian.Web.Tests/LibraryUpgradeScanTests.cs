using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Contracts.Upgrades;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A6-UPGRADE-1 U-2 (alert-only): a delivered copy that recorded evidence calls incomplete is searched again, and a strong new
/// candidate is proposed and announced. Nothing is fetched, changed or deleted. Each scenario has its own database.
/// </summary>
[TestClass]
public sealed class LibraryUpgradeScanTests
{
    private sealed record Setup(FamilyLibrarianAppFactory Factory, UpgradeProviderClient Provider, HttpClient Admin, Guid RequestId, Guid FormatId, Guid WorkId);

    private static async Task<Setup> ArrangeAsync(Action<UpgradeProviderClient>? configure = null)
    {
        var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var provider = new UpgradeProviderClient { CandidateId = "better-1", OutputFormat = "epub" };
        configure?.Invoke(provider);
        var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<IExternalProviderClient>();
            services.AddSingleton<IExternalProviderClient>(provider);
        });
        var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await admin.PostAsJsonAsync("/api/auth/login", new FamilyLibrarian.Contracts.Authentication.LoginRequest
        { Email = FamilyLibrarianAppFactory.AdminEmail, Password = FamilyLibrarianAppFactory.AdminPassword })).EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Add(AntiforgeryTokenEndpoint.HeaderName, await WebTestFixture.GetAntiforgeryTokenAsync(admin));
        var create = await admin.PostAsJsonAsync("/api/v1/admin/external-providers/", new CreateExternalProviderRequest("upgrade-external", "Upgrade Source", "http://fake-external.test"));
        create.EnsureSuccessStatusCode();
        var registered = (await create.Content.ReadFromJsonAsync<ExternalProviderResponse>())!;
        (await admin.PutAsJsonAsync($"/api/v1/admin/external-providers/{registered.Id}/enabled", new SetExternalProviderEnabledRequest(true))).EnsureSuccessStatusCode();
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(work.Id, ["Ebook"], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        var formatId = request.Formats.Single().FormatId;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workId = (await db.BookRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).WorkId;
        await db.BookRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RequestStatus.Available));
        await db.RequestFormats.Where(f => f.Id == formatId).ExecuteUpdateAsync(set => set.SetProperty(f => f.Status, RequestFormatStatus.Available));
        return new Setup(factory, provider, admin, request.Id, formatId, workId);
    }

    /// <summary>Adds the copy that is "in the library": an archived asset with the given evidence.</summary>
    private static async Task<Guid> DeliverAsync(Setup s, AssetOrigin origin, string? deficiencyCode, string recordKind = ArtifactEvidenceRecordKinds.SecurityEvaluation,
        EvidenceDimension dimension = EvidenceDimension.Completeness, EvidencePolarity polarity = EvidencePolarity.Condition, string? extraRecordKind = null)
    {
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(s.WorkId, null, RequestMediaType.Ebook, ".epub", "book.epub", "stored-" + Guid.NewGuid().ToString("N"), 10,
            new string('d', 64), "application/epub+zip", s.FormatId, null, now, origin: origin);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        asset.TransitionStorageState(MediaAssetStorageState.Trusted, now);
        asset.TransitionStorageState(MediaAssetStorageState.Archived, now);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        if (deficiencyCode is not null)
            db.ArtifactEvidenceItems.Add(new ArtifactEvidence(asset.Id, new string('d', 64), recordKind, Guid.NewGuid(), "f1", dimension, deficiencyCode, polarity, "test", null, null, now));
        if (extraRecordKind is not null)
            db.ArtifactEvidenceItems.Add(new ArtifactEvidence(asset.Id, new string('d', 64), extraRecordKind, Guid.NewGuid(), "f2", EvidenceDimension.IdentityAttestation,
                "artifact.identity_attestation.admin_override", EvidencePolarity.Positive, "test", null, null, now));
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static async Task<LibraryUpgradeScanReport> ScanAsync(Setup s, LibraryUpgradeOptions? options = null)
    {
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        return await new LibraryUpgradeScanService(sp.GetRequiredService<ILibraryUpgradeStore>(), sp.GetRequiredService<IExternalProviderStore>(),
            sp.GetRequiredService<FamilyLibrarian.Application.Catalog.ExternalCandidateAvailabilityChecker>(),
            sp.GetRequiredService<FamilyLibrarian.Application.Publishing.IWorkLookup>(), sp.GetRequiredService<FamilyLibrarian.Application.Notifications.NotificationService>(),
            sp.GetRequiredService<FamilyLibrarian.Application.Integrations.IAuditWriter>(), sp.GetRequiredService<IClock>(), options ?? new LibraryUpgradeOptions()).RunAsync(CancellationToken.None);
    }

    private static async Task<List<LibraryUpgrade>> UpgradesAsync(Setup s)
    {
        await using var scope = s.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().LibraryUpgrades.AsNoTracking().Where(u => u.RequestFormatId == s.FormatId).ToListAsync();
    }

    [TestMethod]
    public async Task AnIncompleteDeliveredCopyGetsAProposalAndOneAdminNotificationAndNothingElseChanges()
    {
        var s = await ArrangeAsync();
        await using var factory = s.Factory;
        using var admin = s.Admin;
        var delivered = await DeliverAsync(s, AssetOrigin.ExternalProvider, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing);

        var report = await ScanAsync(s);

        Assert.IsGreaterThanOrEqualTo(1, report.Proposed);
        var upgrade = (await UpgradesAsync(s)).Single();
        Assert.AreEqual(LibraryUpgradeState.Proposed, upgrade.State);
        Assert.AreEqual(delivered, upgrade.DeliveredAssetId);
        Assert.AreEqual("better-1", upgrade.CandidateReference);
        StringAssert.Contains(upgrade.Reasons, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing);
        Assert.AreEqual(0, s.Provider.SubmitCalls, "Alert-only: nothing is fetched.");
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.AreEqual(MediaAssetStorageState.Archived, (await db.MediaAssets.AsNoTracking().SingleAsync(a => a.Id == delivered)).StorageState, "The delivered copy is untouched.");
        Assert.AreEqual(1, await db.NotificationEvents.CountAsync(e => e.Category == NotificationCategories.BetterCopyAvailable && e.SubjectId == s.RequestId.ToString()));

        var searches = s.Provider.SearchCalls;
        var second = await ScanAsync(s);
        Assert.AreEqual(1, (await UpgradesAsync(s)).Count, "Not proposed twice.");
        Assert.AreEqual(0, second.Proposed);
        Assert.AreEqual(searches, s.Provider.SearchCalls, "Inside the 30-day interval the book is not searched again.");

        // Even past the interval, a book with an open proposal is not searched again.
        await db.RequestFormats.Where(f => f.Id == s.FormatId).ExecuteUpdateAsync(set => set.SetProperty(f => f.UpgradeScannedAtUtc, DateTimeOffset.UtcNow.AddDays(-90)));
        await ScanAsync(s);
        Assert.AreEqual(searches, s.Provider.SearchCalls, "An open proposal means nothing more to look for.");
    }

    [TestMethod]
    public async Task OnlyACopyWithAMeasuredSignOfIncompletenessIsEverLookedAt()
    {
        var s = await ArrangeAsync();
        await using var factory = s.Factory;
        using var admin = s.Admin;
        await DeliverAsync(s, AssetOrigin.ExternalProvider, deficiencyCode: null);
        // Evidence is recorded for every file; one that is merely informational (or is a condition that is not a sign of incompleteness) is no reason.
        await DeliverAsync(s, AssetOrigin.ExternalProvider, EvidenceReasonCodes.ArtifactCompleteness.ReadingOrderCounted, polarity: EvidencePolarity.Condition);

        var report = await ScanAsync(s);

        Assert.AreEqual(0, (await UpgradesAsync(s)).Count);
        Assert.AreEqual(0, report.Proposed);
        Assert.AreEqual(0, s.Provider.SearchCalls, "A copy with nothing wrong recorded is never searched for.");
    }

    [TestMethod]
    public async Task ACopyAPersonChoseIsNeverProposedForReplacement()
    {
        var s = await ArrangeAsync();
        await using var factory = s.Factory;
        using var admin = s.Admin;
        await DeliverAsync(s, AssetOrigin.ExternalProvider, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, extraRecordKind: ArtifactEvidenceRecordKinds.IdentityOverride);

        await ScanAsync(s);

        Assert.AreEqual(0, (await UpgradesAsync(s)).Count, "An administrator released this copy; their decision is not undone.");
    }

    [TestMethod]
    public async Task AWeakerCandidateIsNeverProposed()
    {
        var s = await ArrangeAsync(p => p.StatesIdentifiers = false);
        await using var factory = s.Factory;
        using var admin = s.Admin;
        await DeliverAsync(s, AssetOrigin.ExternalProvider, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing);
        // A last-name-only author is a plausible match but not a strong one.
        s.Provider.WeakAuthor = true;

        await ScanAsync(s);

        Assert.AreEqual(0, (await UpgradesAsync(s)).Count, "Only a strong candidate is worth proposing.");
    }

    [TestMethod]
    public async Task AnAdministratorCanSetAProposalAsideAndItIsNotProposedAgain()
    {
        var s = await ArrangeAsync();
        await using var factory = s.Factory;
        using var admin = s.Admin;
        await DeliverAsync(s, AssetOrigin.ExternalProvider, EvidenceReasonCodes.ArtifactCompleteness.DuplicateReadingOrderItems);
        await ScanAsync(s);

        var listed = (await admin.GetFromJsonAsync<LibraryUpgradeResponse[]>("/api/v1/admin/upgrades/"))!.Single(u => u.RequestId == s.RequestId);
        Assert.AreEqual("Proposed", listed.State);
        CollectionAssert.AreEqual(new[] { EvidenceReasonCodes.ArtifactCompleteness.DuplicateReadingOrderItems }, listed.Reasons.ToArray());
        using var member = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/v1/admin/upgrades/")).StatusCode);

        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/admin/upgrades/{listed.Id}/dismiss", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/v1/admin/upgrades/{listed.Id}/dismiss", null)).StatusCode, "A set-aside proposal cannot be set aside again.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/v1/admin/upgrades/{Guid.NewGuid()}/dismiss", null)).StatusCode);

        var searchesBefore = s.Provider.SearchCalls;
        await ScanAsync(s);
        Assert.AreEqual(searchesBefore, s.Provider.SearchCalls, "Inside the interval a set-aside book is not searched again either.");

        // Past the interval, the same candidate is not proposed again.
        await using (var scope = s.Factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().RequestFormats.Where(f => f.Id == s.FormatId)
                .ExecuteUpdateAsync(set => set.SetProperty(f => f.UpgradeScannedAtUtc, DateTimeOffset.UtcNow.AddDays(-90)));
        await ScanAsync(s);
        Assert.AreEqual(1, (await UpgradesAsync(s)).Count, "The candidate that was set aside is remembered.");
        Assert.AreEqual(LibraryUpgradeState.Dismissed, (await UpgradesAsync(s)).Single().State);
    }
}
