using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Upgrades;

public sealed record LibraryUpgradeStartResult(bool Started, string? Reason)
{
    public static LibraryUpgradeStartResult Yes() => new(true, null);

    public static LibraryUpgradeStartResult No(string reason) => new(false, reason);
}

/// <summary>
/// Carries a proposed upgrade out (A6-UPGRADE-1 U-2): fetch the better copy, deliver it NEXT TO the old one, confirm it in the
/// destination, and only then remove the old one. At every step a surprise leaves both copies as they are and tells administrators.
/// The old copy is never removed before the new one is confirmed in the same destination, and never when the destination would not
/// confirm that it may be removed.
/// </summary>
public sealed class LibraryUpgradeService(
    ILibraryUpgradeStore store,
    ISecurityEvaluationRepository assets,
    IAssetStagingStore staging,
    IAudiobookshelfSettingsStore absSettings,
    IAudiobookshelfApiClient abs,
    IAudiobookshelfDeliveryRepository absDeliveries,
    ICwaSettingsStore cwaSettings,
    ILibraryImportRepository cwaImports,
    ICwaCatalogClient cwaCatalog,
    ICwaEreaderSessionClient cwaSession,
    ICredentialProtector protector,
    IWorkLookup workLookup,
    DirectAcquisitionSecurityService acquisition,
    NotificationService notifications,
    IAuditWriter audit,
    IClock clock,
    LibraryUpgradeOptions options)
{
    private static readonly TimeSpan ConfirmationDeadline = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a fetch is left alone before it is asked whether anything is still on its way. It covers the moments between steps
    /// (a source job ending and its file being recorded, an approval and the delivery that follows it) so a fetch in progress is
    /// never called abandoned.
    /// </summary>
    private static readonly TimeSpan AcquisitionGrace = TimeSpan.FromHours(1);

    /// <summary>Starts a proposed upgrade. Called for every proposal when automatic replacement is on, and by an administrator's approval.</summary>
    public async Task<LibraryUpgradeStartResult> StartAsync(Guid upgradeId, bool approvedByAdministrator, CancellationToken cancellationToken)
    {
        var upgrade = await store.FindAsync(upgradeId, cancellationToken);
        if (upgrade is null || upgrade.State != LibraryUpgradeState.Proposed) return LibraryUpgradeStartResult.No("Only a proposal can be started.");
        // The administrator's saved choice wins over the configured default; with neither, it is on.
        var automatic = await store.GetAutoReplaceAsync(cancellationToken) ?? options.AutoReplace;
        if (!approvedByAdministrator && !automatic) return LibraryUpgradeStartResult.No("Automatic replacement is off.");
        var delivered = await assets.FindAssetAsync(upgrade.DeliveredAssetId, cancellationToken);
        if (delivered is null) return LibraryUpgradeStartResult.No("The delivered copy is no longer on record.");
        var readiness = await CheckReadinessAsync(delivered, cancellationToken);
        if (readiness is not null) return LibraryUpgradeStartResult.No(readiness);

        upgrade.MarkAcquiring(clock.UtcNow);
        await store.SaveAsync(cancellationToken);
        await audit.WriteSystemAsync(AuditActions.LibraryUpgradeStarted, AuditSubjectTypes.BookRequest, upgrade.RequestId.ToString(),
            new { UpgradeId = upgrade.Id, upgrade.RequestFormatId, upgrade.ProviderId }, cancellationToken);
        try
        {
            var (result, identityFailure) = await acquisition.AcquireEvaluateAndVerifyAsync(
                upgrade.RequestId, upgrade.RequestFormatId, upgrade.ProviderId, upgrade.CandidateReference, cancellationToken,
                isAutomaticAcquisition: true, allowAdditionalCopy: true);
            if (result.Outcome is ManualImportOutcome.AcquisitionInProgress || result.Outcome == ManualImportOutcome.Success && identityFailure is null)
                return LibraryUpgradeStartResult.Yes();
            await FailAsync(upgrade, identityFailure ?? result.Error ?? "The better copy could not be fetched.", cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await FailAsync(upgrade, "The better copy could not be fetched.", cancellationToken);
        }

        return LibraryUpgradeStartResult.No("The better copy could not be fetched.");
    }

    /// <summary>
    /// Whether the delivered copy can be retracted later: the destination is configured, the credential may delete, and the old copy's
    /// id in the destination is known. Null means ready; anything else is the reason it is not.
    /// </summary>
    private async Task<string?> CheckReadinessAsync(MediaAsset delivered, CancellationToken cancellationToken)
    {
        if (delivered.MediaType == RequestMediaType.Ebook)
        {
            var settings = await cwaSettings.FindAsync(cancellationToken);
            if (settings is null || !settings.IsEnabled) return "CWA is not configured.";
            if (CwaCredentials(settings) is null) return "No CWA account is set up that could remove the old copy.";
            var import = await cwaImports.FindByAssetIdAsync(delivered.Id, cancellationToken);
            return string.IsNullOrWhiteSpace(import?.ExternalBookId) ? "The old copy's place in CWA is not on record." : null;
        }

        var absConfigured = await absSettings.FindAsync(cancellationToken);
        if (absConfigured is null || !absConfigured.IsEnabled) return "Audiobookshelf is not configured.";
        if (!await abs.CanDeleteAsync(cancellationToken)) return "The Audiobookshelf account may not delete items.";
        return string.IsNullOrWhiteSpace(await OldItemIdAsync(delivered, cancellationToken)) ? "The old copy's place in Audiobookshelf is not on record." : null;
    }

    private async Task<string?> OldItemIdAsync(MediaAsset delivered, CancellationToken cancellationToken)
    {
        var delivery = delivered.BundleId is { } bundleId
            ? await absDeliveries.FindByBundleIdAsync(bundleId, cancellationToken)
            : await absDeliveries.FindByAssetIdAsync(delivered.Id, cancellationToken);
        return delivery?.ExternalItemId;
    }

    private (string Username, string Password)? CwaCredentials(CwaSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.EreaderServiceAccountUsername) && settings.HasEreaderServiceAccountPassword
            ? (settings.EreaderServiceAccountUsername!, protector.Unprotect(PublishingSecretPurposes.CwaEreaderServiceAccountPassword,
                settings.ProtectedEreaderServiceAccountPassword!, settings.EreaderServiceAccountPasswordFormatVersion)!)
            : null;

    /// <summary>
    /// Confirms delivered upgrade copies in their destination and removes the old ones. Run on a short timer. One upgrade failing never
    /// stops the others. An upgrade already confirmed (<see cref="LibraryUpgradeState.Verified"/>) is picked up again here, so a restart
    /// between confirming the better copy and removing the old one finishes the job instead of stranding it.
    /// </summary>
    public async Task<int> FinalizeAsync(CancellationToken cancellationToken)
    {
        var done = 0;
        var pending = (await store.ListByStateAsync(LibraryUpgradeState.Publishing, 20, cancellationToken))
            .Concat(await store.ListByStateAsync(LibraryUpgradeState.Verified, 20, cancellationToken));
        foreach (var upgrade in pending)
        {
            try
            {
                if (await FinalizeOneAsync(upgrade, cancellationToken)) done++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (upgrade.State == LibraryUpgradeState.Verified) await RetryOrGiveUpAsync(upgrade, cancellationToken);
                else await FailAsync(upgrade, "Finishing the upgrade failed unexpectedly; both copies were left as they are.", cancellationToken);
            }
        }

        foreach (var upgrade in await store.ListByStateAsync(LibraryUpgradeState.Acquiring, 20, cancellationToken))
        {
            try
            {
                await ReleaseIfAbandonedAsync(upgrade, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Nothing was changed in the library; the same question is asked again on the next pass.
            }
        }

        return done;
    }

    /// <summary>
    /// Ends an upgrade whose better copy will never arrive: the source gave up, or the copy failed its checks or was removed. Without
    /// this the upgrade would stay open for good, and an open upgrade stops the book from ever being looked at again. The copy in the
    /// library is not touched; closing the upgrade is what lets the next scan look for another copy.
    /// </summary>
    private async Task ReleaseIfAbandonedAsync(LibraryUpgrade upgrade, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (now - upgrade.UpdatedAtUtc < AcquisitionGrace) return;
        if (await store.IsAcquisitionInFlightAsync(upgrade, now - AcquisitionGrace, cancellationToken)) return;
        await FailAsync(upgrade, "The better copy could not be fetched or did not pass its checks. The copy in the library was left as it is; another copy will be looked for.", cancellationToken);
    }

    private async Task<bool> FinalizeOneAsync(LibraryUpgrade upgrade, CancellationToken cancellationToken)
    {
        var delivered = await assets.FindAssetAsync(upgrade.DeliveredAssetId, cancellationToken);
        var fresh = upgrade.NewAssetId is { } newId ? await assets.FindAssetAsync(newId, cancellationToken) : null;
        if (delivered is null || fresh is null)
        {
            await FailAsync(upgrade, "A copy is no longer on record; both copies were left as they are.", cancellationToken);
            return false;
        }

        var work = await workLookup.FindAsync(fresh.WorkId, cancellationToken);
        var title = work?.Title ?? "Unknown title";
        var newFiles = fresh.BundleId is { } bundle ? await assets.FindAssetsByBundleIdAsync(bundle, cancellationToken) : [fresh];

        // Every step below can be repeated: the record of the better copy is written once, a copy already removed counts as removed,
        // and the upgrade is only marked complete at the very end. So running this again after an interruption finishes the job.
        if (fresh.MediaType == RequestMediaType.Ebook)
        {
            var settings = await cwaSettings.FindAsync(cancellationToken);
            var oldImport = await cwaImports.FindByAssetIdAsync(delivered.Id, cancellationToken);
            var newImport = await cwaImports.FindByAssetIdAsync(fresh.Id, cancellationToken);
            var newBookId = newImport?.ExternalBookId;
            if (newBookId is null)
            {
                var found = await cwaCatalog.FindImportedFileAsync(title, fresh.Format.TrimStart('.'), fresh.SizeBytes, fresh.Sha256, cancellationToken);
                newBookId = found.Decision == Matching.BookMatchDecision.Match && found.MatchedId != oldImport?.ExternalBookId ? found.MatchedId : null;
            }

            if (newBookId is null) return await WaitOrExpireAsync(upgrade, cancellationToken);
            await MarkVerifiedAsync(upgrade, cancellationToken);

            // The better copy is confirmed in the library, so it is recorded and its local bytes retired now, before the old copy is
            // touched: whatever happens next, the library holds it and the records say so.
            if (newImport is null)
            {
                var import = new LibraryImport(fresh.Id, clock.UtcNow);
                import.MarkAwaitingVerification(PublishingFilenames.BuildTargetFilename(title, fresh.Format));
                import.MarkAvailable(newBookId, clock.UtcNow);
                cwaImports.Add(import);
                await cwaImports.SaveChangesAsync(cancellationToken);
            }

            await RetireStagedCopyAsync(newFiles, cancellationToken);

            var credentials = settings is null ? null : CwaCredentials(settings);
            if (settings is null || credentials is null || oldImport?.ExternalBookId is null)
                return await StopWithBothAsync(upgrade, title, "CWA can no longer remove the old copy. Both copies are in the library; remove the incomplete one by hand.", cancellationToken);
            var removed = await cwaSession.DeleteBookAsync(settings, credentials.Value.Username, credentials.Value.Password, oldImport.ExternalBookId, cancellationToken);
            if (!removed.Succeeded && !removed.AlreadyGone)
                return await StopWithBothAsync(upgrade, title, "CWA would not remove the old copy (" + removed.Message + "). Both copies are in the library; remove the incomplete one by hand.", cancellationToken);
        }
        else
        {
            var oldItemId = await OldItemIdAsync(delivered, cancellationToken);
            var newDelivery = fresh.BundleId is { } freshBundleId
                ? await absDeliveries.FindByBundleIdAsync(freshBundleId, cancellationToken)
                : await absDeliveries.FindByAssetIdAsync(fresh.Id, cancellationToken);
            var newItemId = newDelivery?.ExternalItemId;
            if (newItemId is null)
            {
                newItemId = await abs.FindItemIdByExactTitleAsync(LibraryUpgradeCopyPublisher.TemporaryTitle(title, upgrade.Id), cancellationToken);
                if (newItemId is null && upgrade.State == LibraryUpgradeState.Verified)
                {
                    // Resuming: the temporary title is gone only if the better copy was already renamed. With the old item removed it is
                    // then the one item carrying the real title.
                    var renamed = await abs.FindItemIdByExactTitleAsync(title, cancellationToken);
                    if (renamed is not null && renamed != oldItemId) newItemId = renamed;
                }
            }

            if (newItemId is null || newItemId == oldItemId) return await WaitOrExpireAsync(upgrade, cancellationToken);
            await MarkVerifiedAsync(upgrade, cancellationToken);

            if (newDelivery is null)
            {
                var delivery = fresh.BundleId is { } freshBundle ? AudiobookshelfDelivery.ForBundle(freshBundle, clock.UtcNow) : new AudiobookshelfDelivery(fresh.Id, clock.UtcNow);
                delivery.MarkVerifying();
                delivery.MarkDelivered(newItemId, clock.UtcNow);
                absDeliveries.Add(delivery);
                await absDeliveries.SaveChangesAsync(cancellationToken);
            }

            await RetireStagedCopyAsync(newFiles, cancellationToken);

            // A delete that finds nothing there (the interrupted run already did it) is what was wanted.
            if (oldItemId is null || !await abs.DeleteItemHardAsync(oldItemId, cancellationToken))
                return await StopWithBothAsync(upgrade, title, "Audiobookshelf would not remove the old copy. Both copies are in the library under different names; remove the incomplete one by hand.", cancellationToken);
            // Best effort: the better copy is already the only one, and only its temporary title is left to tidy.
            _ = await abs.RenameItemAsync(newItemId, title, cancellationToken);
        }

        upgrade.MarkCompleted(clock.UtcNow);
        await store.SaveAsync(cancellationToken);
        await audit.WriteSystemAsync(AuditActions.LibraryUpgradeCompleted, AuditSubjectTypes.BookRequest, upgrade.RequestId.ToString(),
            new { UpgradeId = upgrade.Id, upgrade.RequestFormatId }, cancellationToken);
        await notifications.RecordLibraryUpgradedAsync(upgrade.RequestId, title, cancellationToken);
        return true;
    }

    /// <summary>Saves "confirmed in the library" once; a resumed upgrade is already there.</summary>
    private async Task MarkVerifiedAsync(LibraryUpgrade upgrade, CancellationToken cancellationToken)
    {
        if (upgrade.State != LibraryUpgradeState.Publishing) return;
        upgrade.MarkVerified(clock.UtcNow);
        await store.SaveAsync(cancellationToken);
    }

    /// <summary>
    /// The better copy is in the library, so its staged bytes are redundant: the record becomes archived (what the destination
    /// confirmed) and the local file is removed. Only copies still in the trusted zone are touched, so a repeat does nothing.
    /// </summary>
    private async Task RetireStagedCopyAsync(IReadOnlyList<MediaAsset> files, CancellationToken cancellationToken)
    {
        var staged = files.Where(file => file.StorageState == MediaAssetStorageState.Trusted).ToArray();
        if (staged.Length == 0) return;
        foreach (var file in staged) file.TransitionStorageState(MediaAssetStorageState.Archived, clock.UtcNow);
        await assets.SaveChangesAsync(cancellationToken);
        foreach (var file in staged)
        {
            try
            {
                await staging.DeleteAsync(MediaAssetStorageState.Trusted, file.StoredFilename, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A leftover file in the trusted zone is for the clean-up sweep; it is never a reason to undo a finished upgrade.
            }
        }
    }

    /// <summary>
    /// A step after the better copy was confirmed failed. The old copy may already be gone, so nothing is claimed about it: the step is
    /// simply tried again on the next pass, and only after a day of failing is the administrator told to look.
    /// </summary>
    private async Task RetryOrGiveUpAsync(LibraryUpgrade upgrade, CancellationToken cancellationToken)
    {
        if (clock.UtcNow - upgrade.UpdatedAtUtc <= ConfirmationDeadline) return;
        await FailAsync(upgrade,
            "Finishing the upgrade kept failing after the better copy was confirmed in the library. The old copy may already have been removed; check the library and remove any duplicate by hand.",
            cancellationToken);
    }

    /// <summary>Not confirmed yet: keep waiting, but never past the deadline. Past it, both copies stay and administrators are told.</summary>
    private async Task<bool> WaitOrExpireAsync(LibraryUpgrade upgrade, CancellationToken cancellationToken)
    {
        if (clock.UtcNow - upgrade.UpdatedAtUtc > ConfirmationDeadline)
            await FailAsync(upgrade, "The better copy was not confirmed in the library within a day. Both copies were left as they are.", cancellationToken);
        return false;
    }

    private async Task<bool> StopWithBothAsync(LibraryUpgrade upgrade, string title, string reason, CancellationToken cancellationToken)
    {
        await FailAsync(upgrade, reason, cancellationToken, title);
        return false;
    }

    private async Task FailAsync(LibraryUpgrade upgrade, string reason, CancellationToken cancellationToken, string? title = null)
    {
        upgrade.MarkFailed(reason, clock.UtcNow);
        await store.SaveAsync(cancellationToken);
        await audit.WriteSystemAsync(AuditActions.LibraryUpgradeFailed, AuditSubjectTypes.BookRequest, upgrade.RequestId.ToString(),
            new { UpgradeId = upgrade.Id, upgrade.RequestFormatId, Reason = reason }, cancellationToken);
        var name = title ?? (await workLookup.FindAsync((await assets.FindAssetAsync(upgrade.DeliveredAssetId, cancellationToken))?.WorkId ?? Guid.Empty, cancellationToken))?.Title ?? "a book";
        await notifications.RecordUpgradeStoppedAsync(upgrade.RequestId, name, reason, cancellationToken);
    }
}
