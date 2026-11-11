using System.Net.Http.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Upgrades;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Notifications;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using FamilyLibrarian.Web.Tests.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// A6-UPGRADE-1 U-2, the replacement itself: the better copy is delivered next to the old one, confirmed in the destination, and only
/// then is the old one removed. Any surprise leaves both copies and tells administrators. Destinations are in-memory fakes so the order
/// of their calls can be asserted; everything else is real (PostgreSQL, staging files, security pipeline).
/// </summary>
[TestClass]
public sealed class LibraryUpgradeReplacementTests
{
    private const string OldBookId = "100";
    private const string NewBookId = "200";
    private const string OldItemId = "old-item";

    private sealed class Calls
    {
        public List<string> Log { get; } = [];
    }

    private sealed class FakeCwa(Calls calls) : ICwaIngestTransportFactory, ICwaIngestTransport, ICwaCatalogClient, ICwaEreaderSessionClient
    {
        public bool NewCopyVisible { get; set; } = true;

        public bool ReportOldAsNew { get; set; }

        public CwaDeleteResult DeleteResult { get; set; } = new(true, null);

        public int Written { get; private set; }

        public ICwaIngestTransport Create(CwaSettings settings) => this;

        public async Task WriteAsync(Stream content, string targetFilename, CancellationToken cancellationToken)
        {
            await content.CopyToAsync(Stream.Null, cancellationToken);
            Written++;
            calls.Log.Add("cwa.write");
        }

        public Task TouchAsync(string targetFilename, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BookMatchResult> FindBookIdAsync(string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken, string? acceptedLanguage = null) =>
            Task.FromResult(BookMatchResult.NoMatchResult);

        public Task<BookMatchResult> FindImportedFileAsync(string title, string format, long sizeBytes, string sha256, CancellationToken cancellationToken, string? acceptedLanguage = null)
        {
            calls.Log.Add("cwa.verify");
            return Task.FromResult(!NewCopyVisible ? BookMatchResult.NoMatchResult : BookMatchResult.Match(new CandidateBook(ReportOldAsNew ? OldBookId : NewBookId, "t", null)));
        }

        public Task<CwaDeleteResult> DeleteBookAsync(CwaSettings settings, string serviceAccountUsername, string serviceAccountPassword, string cwaBookId, CancellationToken cancellationToken)
        {
            calls.Log.Add("cwa.delete:" + cwaBookId);
            return Task.FromResult(DeleteResult);
        }

        public Task<CwaEreaderSendResult> SendSelectedAsync(CwaSettings settings, string serviceAccountUsername, string serviceAccountPassword, string cwaBookId, string bookFormat, bool convert, string recipientEmail, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CwaEreaderLoginResult> TestLoginAsync(CwaSettings settings, string serviceAccountUsername, string serviceAccountPassword, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAbs(Calls calls) : IAudiobookshelfApiClient
    {
        public bool CanDelete { get; set; } = true;

        public bool DeleteWorks { get; set; } = true;

        public bool NewItemVisible { get; set; } = true;

        public string? UploadedTitle { get; private set; }

        public Task<BookMatchResult> FindExistingItemIdAsync(string title, string? author, CancellationToken cancellationToken, string? acceptedLanguage = null) => Task.FromResult(BookMatchResult.NoMatchResult);

        public Task<bool> CanDeleteAsync(CancellationToken cancellationToken) => Task.FromResult(CanDelete);

        public Task<string?> FindItemIdByExactTitleAsync(string exactTitle, CancellationToken cancellationToken)
        {
            calls.Log.Add("abs.verify");
            return Task.FromResult(NewItemVisible && exactTitle == UploadedTitle ? "new-item" : null);
        }

        public Task<bool> DeleteItemHardAsync(string itemId, CancellationToken cancellationToken)
        {
            calls.Log.Add("abs.delete:" + itemId);
            return Task.FromResult(DeleteWorks);
        }

        public Task<bool> RenameItemAsync(string itemId, string title, CancellationToken cancellationToken)
        {
            calls.Log.Add("abs.rename:" + itemId);
            return Task.FromResult(true);
        }

        public Task<AudiobookshelfUploadResult> UploadAsync(Stream content, string filename, string title, string? author, CancellationToken cancellationToken) =>
            UploadBundleAsync([(content, filename)], title, author, cancellationToken);

        public Task<AudiobookshelfUploadResult> UploadBundleAsync(IReadOnlyList<(Stream Content, string Filename)> tracks, string title, string? author, CancellationToken cancellationToken)
        {
            UploadedTitle = title;
            calls.Log.Add("abs.upload:" + title);
            return Task.FromResult(new AudiobookshelfUploadResult(true, null, null));
        }
    }

    private sealed record Arranged(FamilyLibrarianAppFactory Factory, Calls Calls, FakeCwa Cwa, FakeAbs Abs, ParkedRequestUpgradeEndToEndTests.MutableClock Clock,
        Guid RequestId, Guid FormatId, Guid WorkId, Guid OldAssetId, Guid UpgradeId, UpgradeProviderClient Provider, HttpClient Admin) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            await Factory.DisposeAsync();
        }

        public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public Task<LibraryUpgrade> UpgradeAsync() => WithDb(db => db.LibraryUpgrades.AsNoTracking().SingleAsync(u => u.Id == UpgradeId));
    }

    private static async Task<Arranged> ArrangeAsync(RequestMediaType type, bool withDeleteAccount = true, bool acquiring = true, Action<UpgradeProviderClient>? configure = null)
    {
        var fixture = WebTestFixture.Require(await WebTestFixture.CreateAsync());
        var calls = new Calls();
        var cwa = new FakeCwa(calls);
        var abs = new FakeAbs(calls);
        var provider = new UpgradeProviderClient { CandidateId = "better-1", OutputFormat = "epub" };
        configure?.Invoke(provider);
        var clock = new ParkedRequestUpgradeEndToEndTests.MutableClock(DateTimeOffset.UtcNow);
        var factory = new FamilyLibrarianAppFactory(fixture.ConnectionString, services =>
        {
            services.RemoveAll<ICwaIngestTransportFactory>();
            services.AddSingleton<ICwaIngestTransportFactory>(cwa);
            services.RemoveAll<ICwaCatalogClient>();
            services.AddSingleton<ICwaCatalogClient>(cwa);
            services.RemoveAll<ICwaEreaderSessionClient>();
            services.AddSingleton<ICwaEreaderSessionClient>(cwa);
            services.RemoveAll<IAudiobookshelfApiClient>();
            services.AddSingleton<IAudiobookshelfApiClient>(abs);
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
        var resolve = await admin.PostAsync("/api/v1/catalog/candidates/demo/the-hobbit/resolve", content: null);
        var work = (await resolve.Content.ReadFromJsonAsync<CatalogWorkResponse>())!;
        var created = await admin.PostAsJsonAsync("/api/v1/requests/", new CreateBookRequestRequest(work.Id, [type.ToString()], null, false, false));
        var request = (await created.Content.ReadFromJsonAsync<BookRequestResponse>())!;
        var formatId = request.Formats.Single().FormatId;

        await using var scope = factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var protector = sp.GetRequiredService<ICredentialProtector>();
        var now = DateTimeOffset.UtcNow;
        var workId = (await db.BookRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).WorkId;
        await db.BookRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RequestStatus.Available));
        await db.RequestFormats.Where(f => f.Id == formatId).ExecuteUpdateAsync(set => set.SetProperty(f => f.Status, RequestFormatStatus.Available));

        var cwaSettings = new CwaSettings(now);
        cwaSettings.SetSettings(CwaTransportMode.Local, "/tmp/unused", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey, "http://cwa.test", null, null,
            withDeleteAccount ? "svc" : null, null, now);
        if (withDeleteAccount)
            cwaSettings.SetEreaderServiceAccountPassword(protector.Protect(PublishingSecretPurposes.CwaEreaderServiceAccountPassword, "pw"), protector.FormatVersion, null, null, now);
        cwaSettings.SetEnabled(true, null, now);
        db.CwaSettings.Add(cwaSettings);
        var absSettings = new AudiobookshelfSettings(now);
        absSettings.SetSettings("http://abs.test", null, "lib", null, null, now);
        absSettings.SetApiToken(protector.Protect(PublishingSecretPurposes.AudiobookshelfApiToken, "token"), protector.FormatVersion, null, null, now);
        absSettings.SetEnabled(true, null, now);
        db.AudiobookshelfSettings.Add(absSettings);

        var old = new MediaAsset(workId, null, type, type == RequestMediaType.Ebook ? ".epub" : ".mp3", "old", "stored-" + Guid.NewGuid().ToString("N"), 10,
            new string('a', 64), "application/octet-stream", formatId, null, now, origin: AssetOrigin.ExternalProvider);
        old.TransitionStorageState(MediaAssetStorageState.Processing, now);
        old.TransitionStorageState(MediaAssetStorageState.Trusted, now);
        old.TransitionStorageState(MediaAssetStorageState.Archived, now);
        db.MediaAssets.Add(old);
        await db.SaveChangesAsync();
        db.ArtifactEvidenceItems.Add(new ArtifactEvidence(old.Id, new string('a', 64), ArtifactEvidenceRecordKinds.SecurityEvaluation, Guid.NewGuid(), "f1",
            EvidenceDimension.Completeness, EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing, EvidencePolarity.Condition, "test", null, null, now));
        if (type == RequestMediaType.Ebook)
        {
            var import = new LibraryImport(old.Id, now);
            import.MarkAwaitingVerification("old.epub");
            import.MarkAvailable(OldBookId, now);
            db.LibraryImports.Add(import);
        }
        else
        {
            var delivery = new AudiobookshelfDelivery(old.Id, now);
            delivery.MarkVerifying();
            delivery.MarkDelivered(OldItemId, now);
            db.Deliveries.Add(delivery);
        }

        var upgrade = new LibraryUpgrade(request.Id, formatId, old.Id, "upgrade-external", "better-1", null, [EvidenceReasonCodes.ArtifactCompleteness.TocTargetsMissing], now);
        if (acquiring) upgrade.MarkAcquiring(now);
        db.LibraryUpgrades.Add(upgrade);
        await db.SaveChangesAsync();
        return new Arranged(factory, calls, cwa, abs, clock, request.Id, formatId, workId, old.Id, upgrade.Id, provider, admin);
    }

    /// <summary>The better copy as it looks once approved: trusted, with its real bytes in the trusted zone.</summary>
    private static async Task<MediaAsset> NewTrustedCopyAsync(Arranged a, RequestMediaType type)
    {
        await using var scope = a.Factory.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var staging = sp.GetRequiredService<IAssetStagingStore>();
        await using var content = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("better-" + Guid.NewGuid()));
        var staged = await staging.WriteToQuarantineAsync(content, "better.bin", 1_000_000, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        var asset = new MediaAsset(a.WorkId, null, type, type == RequestMediaType.Ebook ? ".epub" : ".mp3", "better", staged.StoredFilename, staged.SizeBytes,
            staged.Sha256, staged.DetectedMimeType, a.FormatId, null, now, origin: AssetOrigin.ExternalProvider);
        asset.TransitionStorageState(MediaAssetStorageState.Processing, now);
        await staging.MoveAsync(MediaAssetStorageState.Quarantine, MediaAssetStorageState.Processing, staged.StoredFilename, CancellationToken.None);
        asset.TransitionStorageState(MediaAssetStorageState.Trusted, now);
        await staging.MoveAsync(MediaAssetStorageState.Processing, MediaAssetStorageState.Trusted, staged.StoredFilename, CancellationToken.None);
        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    private static async Task<bool> PublishAlongsideAsync(Arranged a, MediaAsset copy)
    {
        await using var scope = a.Factory.Services.CreateAsyncScope();
        var tracked = await scope.ServiceProvider.GetRequiredService<AppDbContext>().MediaAssets.SingleAsync(x => x.Id == copy.Id);
        return await scope.ServiceProvider.GetRequiredService<IUpgradeCopyPublisher>().TryPublishAlongsideAsync([tracked], CancellationToken.None);
    }

    private static async Task<int> FinalizeAsync(Arranged a)
    {
        await using var scope = a.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryUpgradeService>().FinalizeAsync(CancellationToken.None);
    }

    private static bool TrustedFileExists(Arranged a, MediaAsset copy) =>
        Directory.Exists(a.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FamilyLibrarian.Infrastructure.Acquisition.StorageOptions>>().Value.RootPath) &&
        Directory.EnumerateFiles(a.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FamilyLibrarian.Infrastructure.Acquisition.StorageOptions>>().Value.RootPath,
            copy.StoredFilename, SearchOption.AllDirectories).Any();

    private static async Task<int> NotificationsAsync(Arranged a, string category) =>
        await a.WithDb(db => db.NotificationEvents.CountAsync(e => e.Category == category && e.SubjectId == a.RequestId.ToString()));

    [TestMethod]
    public async Task AnEbookUpgradeIsDeliveredNextToTheOldCopyConfirmedAndOnlyThenTheOldOneIsRemoved()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook);
        var copy = await NewTrustedCopyAsync(a, RequestMediaType.Ebook);

        Assert.IsTrue(await PublishAlongsideAsync(a, copy), "The better copy takes the upgrade path.");
        Assert.AreEqual(LibraryUpgradeState.Publishing, (await a.UpgradeAsync()).State);
        Assert.AreEqual(1, a.Cwa.Written);
        CollectionAssert.DoesNotContain(a.Calls.Log, "cwa.delete:" + OldBookId, "Nothing is removed on delivery.");

        Assert.AreEqual(1, await FinalizeAsync(a));

        CollectionAssert.AreEqual(new[] { "cwa.write", "cwa.verify", "cwa.delete:" + OldBookId }, a.Calls.Log, "Deliver, confirm in the destination, then remove the old copy.");
        var upgrade = await a.UpgradeAsync();
        Assert.AreEqual(LibraryUpgradeState.Completed, upgrade.State);
        var after = await a.WithDb(db => db.MediaAssets.AsNoTracking().Where(x => x.Id == copy.Id || x.Id == a.OldAssetId).ToListAsync());
        Assert.AreEqual(MediaAssetStorageState.Archived, after.Single(x => x.Id == copy.Id).StorageState);
        Assert.IsFalse(TrustedFileExists(a, copy), "The local copy is removed once the library holds it.");
        Assert.AreEqual(NewBookId, (await a.WithDb(db => db.LibraryImports.AsNoTracking().SingleAsync(i => i.AssetId == copy.Id))).ExternalBookId, "The new copy's place is recorded for any later upgrade.");
        Assert.AreEqual(1, await NotificationsAsync(a, NotificationCategories.LibraryUpgraded));
    }

    [TestMethod]
    public async Task IfTheDestinationWillNotRemoveTheOldCopyBothStayAndAdministratorsAreTold()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook);
        a.Cwa.DeleteResult = new CwaDeleteResult(false, "no permission");
        var copy = await NewTrustedCopyAsync(a, RequestMediaType.Ebook);
        await PublishAlongsideAsync(a, copy);

        var finished = await FinalizeAsync(a);

        Assert.AreEqual(0, finished);
        var upgrade = await a.UpgradeAsync();
        Assert.AreEqual(LibraryUpgradeState.Failed, upgrade.State);
        StringAssert.Contains(upgrade.FailureReason, "Both copies");
        Assert.AreEqual(1, await NotificationsAsync(a, NotificationCategories.UpgradeStopped));
        Assert.AreEqual(0, await NotificationsAsync(a, NotificationCategories.LibraryUpgraded));
    }

    [TestMethod]
    public async Task NothingIsRemovedUntilTheBetterCopyIsConfirmedAndAnUnconfirmedOneIsAbandonedAfterADay()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook);
        a.Cwa.NewCopyVisible = false;
        var copy = await NewTrustedCopyAsync(a, RequestMediaType.Ebook);
        await PublishAlongsideAsync(a, copy);

        await FinalizeAsync(a);
        Assert.AreEqual(LibraryUpgradeState.Publishing, (await a.UpgradeAsync()).State, "Still waiting for CWA to import it.");

        a.Clock.Advance(TimeSpan.FromHours(25));
        await FinalizeAsync(a);

        Assert.AreEqual(LibraryUpgradeState.Failed, (await a.UpgradeAsync()).State);
        Assert.IsFalse(a.Calls.Log.Any(call => call.StartsWith("cwa.delete", StringComparison.Ordinal)), "The old copy was never touched.");
    }

    [TestMethod]
    public async Task ADestinationThatReportsTheOldCopyAsTheNewOneIsNotTreatedAsConfirmation()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook);
        a.Cwa.ReportOldAsNew = true;
        var copy = await NewTrustedCopyAsync(a, RequestMediaType.Ebook);
        await PublishAlongsideAsync(a, copy);

        await FinalizeAsync(a);

        Assert.AreEqual(LibraryUpgradeState.Publishing, (await a.UpgradeAsync()).State);
        Assert.IsFalse(a.Calls.Log.Any(call => call.StartsWith("cwa.delete", StringComparison.Ordinal)), "Confirming the old copy would delete the only good one.");
    }

    [TestMethod]
    public async Task AnAudiobookUpgradeUsesATemporaryTitleThenRemovesTheOldItemAndRenamesTheNewOne()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Audiobook);
        var copy = await NewTrustedCopyAsync(a, RequestMediaType.Audiobook);

        Assert.IsTrue(await PublishAlongsideAsync(a, copy));
        var temporary = a.Abs.UploadedTitle!;
        StringAssert.Contains(temporary, "(upgrade ");
        Assert.AreEqual(1, await FinalizeAsync(a));

        CollectionAssert.AreEqual(new[] { "abs.upload:" + temporary, "abs.verify", "abs.delete:" + OldItemId, "abs.rename:new-item" }, a.Calls.Log);
        Assert.AreEqual(LibraryUpgradeState.Completed, (await a.UpgradeAsync()).State);
        Assert.AreEqual("new-item", (await a.WithDb(db => db.Deliveries.AsNoTracking().SingleAsync(d => d.AssetId == copy.Id))).ExternalItemId);
    }

    [TestMethod]
    public async Task AnAudiobookIsNotRemovedWhenTheDestinationRefusesTheDelete()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Audiobook);
        a.Abs.DeleteWorks = false;
        var copy = await NewTrustedCopyAsync(a, RequestMediaType.Audiobook);
        await PublishAlongsideAsync(a, copy);

        await FinalizeAsync(a);

        Assert.AreEqual(LibraryUpgradeState.Failed, (await a.UpgradeAsync()).State);
        Assert.IsFalse(a.Calls.Log.Any(call => call.StartsWith("abs.rename", StringComparison.Ordinal)), "Nothing was renamed after a refused delete.");
        Assert.AreEqual(1, await NotificationsAsync(a, NotificationCategories.UpgradeStopped));
    }

    [TestMethod]
    public async Task ACopyThatIsNotTheBetterCopyOfAnUpgradeInProgressTakesTheNormalPath()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook, acquiring: false);
        var other = await NewTrustedCopyAsync(a, RequestMediaType.Ebook);

        Assert.IsFalse(await PublishAlongsideAsync(a, other), "A proposal that has not started is not an upgrade in progress.");

        await using var scope = a.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var upgrade = await db.LibraryUpgrades.SingleAsync(u => u.Id == a.UpgradeId);
        upgrade.MarkAcquiring(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        var delivered = await db.MediaAssets.SingleAsync(x => x.Id == a.OldAssetId);
        Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<IUpgradeCopyPublisher>().TryPublishAlongsideAsync([delivered], CancellationToken.None),
            "The delivered copy itself is never treated as its own upgrade.");
        Assert.AreEqual(0, a.Cwa.Written);
    }

    [TestMethod]
    public async Task AnUpgradeIsNotStartedWhenTheDestinationCannotRemoveTheOldCopy()
    {
        await using var noAccount = await ArrangeAsync(RequestMediaType.Ebook, withDeleteAccount: false, acquiring: false);
        var refusedEbook = await StartAsync(noAccount, approved: false);
        Assert.IsFalse(refusedEbook.Started);
        StringAssert.Contains(refusedEbook.Reason, "No CWA account");
        Assert.AreEqual(LibraryUpgradeState.Proposed, (await noAccount.UpgradeAsync()).State, "Still only a proposal: alert-only.");

        await using var cannotDelete = await ArrangeAsync(RequestMediaType.Audiobook, acquiring: false);
        cannotDelete.Abs.CanDelete = false;
        var refusedAudiobook = await StartAsync(cannotDelete, approved: true);
        Assert.IsFalse(refusedAudiobook.Started);
        StringAssert.Contains(refusedAudiobook.Reason, "may not delete");
        Assert.AreEqual(LibraryUpgradeState.Proposed, (await cannotDelete.UpgradeAsync()).State);
        Assert.AreEqual(0, cannotDelete.Provider.SubmitCalls, "Nothing was fetched.");
    }

    [TestMethod]
    public async Task WithAutomaticReplacementOffAProposalWaitsForAnAdministrator()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook, acquiring: false);
        await using var scope = a.Factory.Services.CreateAsyncScope();
        var off = new LibraryUpgradeOptions { AutoReplace = false };
        var sp = scope.ServiceProvider;
        var service = ActivatorUtilities.CreateInstance<LibraryUpgradeService>(sp, off);

        var result = await service.StartAsync(a.UpgradeId, approvedByAdministrator: false, CancellationToken.None);

        Assert.IsFalse(result.Started);
        StringAssert.Contains(result.Reason, "off");
        Assert.AreEqual(LibraryUpgradeState.Proposed, (await a.UpgradeAsync()).State);
    }

    [TestMethod]
    public async Task FromProposalToReplacementEndToEndTheBetterCopyIsFetchedCheckedDeliveredConfirmedAndTheOldOneRemoved()
    {
        await using var a = await ArrangeAsync(RequestMediaType.Ebook, acquiring: false);

        var started = await StartAsync(a, approved: false);

        Assert.IsTrue(started.Started, started.Reason);
        Assert.AreEqual(LibraryUpgradeState.Acquiring, (await a.UpgradeAsync()).State);
        for (var round = 0; round < 3 && (await a.UpgradeAsync()).State == LibraryUpgradeState.Acquiring; round++)
        {
            await using var scope = a.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AcquisitionJobPollingService>().ProcessDueAsync(CancellationToken.None);
        }

        Assert.AreEqual(1, a.Provider.SubmitCalls, "The better copy was fetched exactly once.");
        Assert.AreEqual(LibraryUpgradeState.Publishing, (await a.UpgradeAsync()).State, "It passed the checks and was delivered next to the old one.");
        Assert.AreEqual(1, a.Cwa.Written);
        Assert.IsFalse(a.Calls.Log.Any(call => call.StartsWith("cwa.delete", StringComparison.Ordinal)), "The old copy is still there.");

        Assert.AreEqual(1, await FinalizeAsync(a));

        Assert.AreEqual(LibraryUpgradeState.Completed, (await a.UpgradeAsync()).State);
        CollectionAssert.AreEqual(new[] { "cwa.write", "cwa.verify", "cwa.delete:" + OldBookId }, a.Calls.Log);
        Assert.AreEqual(1, await NotificationsAsync(a, NotificationCategories.LibraryUpgraded));
    }

    private static async Task<LibraryUpgradeStartResult> StartAsync(Arranged a, bool approved)
    {
        await using var scope = a.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryUpgradeService>().StartAsync(a.UpgradeId, approved, CancellationToken.None);
    }
}
