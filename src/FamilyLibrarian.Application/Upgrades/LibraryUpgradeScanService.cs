using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Upgrades;

/// <summary>How often and how many delivered copies are looked at. Clamped so a typo cannot turn this into a search storm.</summary>
public sealed record LibraryUpgradeOptions
{
    /// <summary>A delivered copy is looked at again no sooner than this (30 days by default, never under a week).</summary>
    public int ScanIntervalDays { get; init; } = 30;

    /// <summary>Most delivered copies searched in one pass.</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>
    /// On by default: a proposal for which the destination can be changed (the ebook or audiobook library has a credential that may delete)
    /// is carried out without waiting for an administrator. Off means every proposal waits for one. Either way nothing is retracted until
    /// the better copy is confirmed in the destination.
    /// </summary>
    public bool AutoReplace { get; init; } = true;

    public LibraryUpgradeOptions Clamped() => this with { ScanIntervalDays = Storage.MaintenanceLimits.Clamp(ScanIntervalDays, Storage.MaintenanceLimits.UpgradeScanIntervalDays), BatchSize = Math.Clamp(BatchSize, 1, 100) };
}

/// <summary>A source's release already known for a format: the delivered copy's, or one proposed and then dismissed or failed.</summary>
public sealed record KnownCandidate(string ProviderId, string Reference, string? Fingerprint);

/// <summary>One delivered copy worth a look, with what is needed to judge it. Ids and codes only.</summary>
public sealed record UpgradeSubject(
    Guid RequestId, Guid RequestFormatId, Guid WorkId, RequestMediaType MediaType, Guid DeliveredAssetId,
    DeliveredCopyFacts Copy, IReadOnlyList<KnownCandidate> Known);

/// <summary>A proposal with what an administrator needs to recognise the book. Titles appear only here, on administrator pages.</summary>
public sealed record LibraryUpgradeView(LibraryUpgrade Upgrade, string WorkTitle, RequestMediaType MediaType);

public interface ILibraryUpgradeStore
{
    /// <summary>
    /// Finished formats whose delivered copy was not looked at since <paramref name="notScannedSince"/> and that have no open upgrade.
    /// </summary>
    Task<IReadOnlyList<UpgradeSubject>> ListSubjectsAsync(DateTimeOffset notScannedSince, int maximumCount, CancellationToken cancellationToken);

    /// <summary>Records that this format's delivered copy was looked at, so it is not searched again until the interval passes.</summary>
    Task MarkScannedAsync(Guid requestFormatId, DateTimeOffset atUtc, CancellationToken cancellationToken);

    void Add(LibraryUpgrade upgrade);

    Task<LibraryUpgrade?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<LibraryUpgrade?> FindOpenForFormatAsync(Guid requestFormatId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryUpgrade>> ListByStateAsync(LibraryUpgradeState state, int maximumCount, CancellationToken cancellationToken);

    Task<IReadOnlyList<LibraryUpgradeView>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken);

    /// <summary>The administrator's choice about automatic replacement, or null when they have never changed it (the default then applies).</summary>
    Task<bool?> GetAutoReplaceAsync(CancellationToken cancellationToken);

    Task SetAutoReplaceAsync(bool autoReplace, Guid? actorUserId, DateTimeOffset atUtc, CancellationToken cancellationToken);

    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed record LibraryUpgradeScanReport(int Looked, int Proposed, int Failures);

/// <summary>
/// Looks for a better copy of delivered books that recorded evidence says are incomplete. It only ever records a proposal and tells
/// administrators; it changes nothing in the library. A candidate is proposed only if it is strong, not the delivered release, and
/// not one already proposed and set aside.
/// </summary>
public sealed class LibraryUpgradeScanService(
    ILibraryUpgradeStore store,
    IExternalProviderStore providers,
    ExternalCandidateAvailabilityChecker checker,
    IWorkLookup workLookup,
    NotificationService notifications,
    IAuditWriter audit,
    IClock clock,
    LibraryUpgradeOptions options,
    LibraryUpgradeService? upgrades = null)
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(2);

    public Task<LibraryUpgradeScanReport> RunAsync(CancellationToken cancellationToken) => RunAsync(options, cancellationToken);

    /// <summary>Runs with the given settings (the administrator's saved interval), not the ones this service was built with.</summary>
    public async Task<LibraryUpgradeScanReport> RunAsync(LibraryUpgradeOptions effective, CancellationToken cancellationToken)
    {
        var o = effective.Clamped();
        var enabled = (await providers.ListEnabledAsync(cancellationToken))
            .Where(provider => !ExternalCandidateAvailabilityChecker.IsKnownSearchUnavailable(provider)).ToArray();
        if (enabled.Length == 0) return new LibraryUpgradeScanReport(0, 0, 0);

        var subjects = await store.ListSubjectsAsync(clock.UtcNow - TimeSpan.FromDays(o.ScanIntervalDays), o.BatchSize, cancellationToken);
        int looked = 0, proposed = 0, failures = 0;
        foreach (var subject in subjects)
        {
            var assessment = DeficientCopyDetector.Assess(subject.Copy);
            if (!assessment.EligibleForUpgrade) continue;
            looked++;
            try
            {
                if (await ProposeAsync(subject, assessment, enabled, cancellationToken)) proposed++;
                await store.MarkScannedAsync(subject.RequestFormatId, clock.UtcNow, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One book failing to search (a provider error, a bad record) never stops the rest.
                failures++;
            }
        }

        return new LibraryUpgradeScanReport(looked, proposed, failures);
    }

    private async Task<bool> ProposeAsync(
        UpgradeSubject subject, DeficiencyAssessment assessment, IReadOnlyList<Domain.Providers.ExternalProvider> enabled, CancellationToken cancellationToken)
    {
        var work = await workLookup.FindAsync(subject.WorkId, cancellationToken);
        if (work is null) return false;
        var identity = new BookIdentity(work.Title, work.PrimaryAuthor, work.Isbn13s, work.Authors, work.Series, work.Language,
            work.PublicationYear, work.Publisher, work.AlternateTitles);
        var variant = SearchVariantPlanner.Plan(identity, subject.MediaType)[0];
        foreach (var provider in enabled)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(SearchTimeout);
            var found = await checker.SearchVariantAsync(provider, identity, subject.MediaType, variant, limit.Token);
            var better = ExternalCandidateRanker.Rank(found.Where(option => IsNewStrongCandidate(option, provider, subject)), subject.MediaType) is { Count: > 0 } ranked ? ranked[0] : null;
            if (better is null) continue;

            var upgrade = new LibraryUpgrade(subject.RequestId, subject.RequestFormatId, subject.DeliveredAssetId, provider.ProviderId,
                better.ProviderResultId, ExternalReleaseFingerprint.Compute(better.ReleaseName, better.SizeBytes), assessment.Reasons, clock.UtcNow);
            store.Add(upgrade);
            await store.SaveAsync(cancellationToken);
            await audit.WriteSystemAsync(AuditActions.LibraryUpgradeProposed, AuditSubjectTypes.BookRequest, subject.RequestId.ToString(),
                new { UpgradeId = upgrade.Id, subject.RequestFormatId, Provider = provider.ProviderId, Reasons = assessment.Reasons }, cancellationToken);
            await notifications.RecordBetterCopyAvailableAsync(subject.RequestId, work.Title, cancellationToken);
            // On by default: carry it out when the destination can be changed. Otherwise (or when switched off) it waits for an administrator.
            if (upgrades is not null && await store.GetAutoReplaceAsync(cancellationToken) is not false && options.AutoReplace)
            {
                await upgrades.StartAsync(upgrade.Id, approvedByAdministrator: false, cancellationToken);
            }

            return true;
        }

        return false;
    }

    private static bool IsNewStrongCandidate(FulfillmentOption option, Domain.Providers.ExternalProvider provider, UpgradeSubject subject)
    {
        if (CandidateConfidence.Classify(option) != CandidateConfidenceTier.Strong) return false;
        if (!CandidateConfidence.Allows(provider.EffectiveConfidence(subject.MediaType), CandidateConfidenceTier.Strong)) return false;
        if (subject.MediaType == RequestMediaType.Audiobook &&
            !(string.IsNullOrWhiteSpace(option.Format) || AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(option.Format))) return false;
        var fingerprint = ExternalReleaseFingerprint.Compute(option.ReleaseName, option.SizeBytes);
        return !subject.Known.Any(known =>
            string.Equals(known.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            (known.Reference == option.ProviderResultId || fingerprint is not null && known.Fingerprint == fingerprint));
    }
}

public sealed record LibraryUpgradeAdminResult(bool Succeeded, bool Forbidden, bool NotFound, string? Error)
{
    public static LibraryUpgradeAdminResult Ok() => new(true, false, false, null);

    public static LibraryUpgradeAdminResult ForbiddenResult() => new(false, true, false, null);

    public static LibraryUpgradeAdminResult Missing() => new(false, false, true, null);

    public static LibraryUpgradeAdminResult Invalid(string error) => new(false, false, false, error);
}

/// <summary>Administrator view of proposed upgrades, and the one action available while nothing has been changed: set a proposal aside.</summary>
public sealed class LibraryUpgradeAdminService(
    ILibraryUpgradeStore store, ICurrentUser currentUser, ICurrentAdministrator administrator, IAuditWriter audit, IClock clock,
    LibraryUpgradeService service, LibraryUpgradeOptions options)
{
    /// <summary>Starts a proposal now: the way to carry one out when automatic replacement is off. Refused, with the reason, when the destination cannot be changed safely.</summary>
    public async Task<LibraryUpgradeAdminResult> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!IsAdmin) return LibraryUpgradeAdminResult.ForbiddenResult();
        if (await store.FindAsync(id, cancellationToken) is null) return LibraryUpgradeAdminResult.Missing();
        var started = await service.StartAsync(id, approvedByAdministrator: true, cancellationToken);
        return started.Started ? LibraryUpgradeAdminResult.Ok() : LibraryUpgradeAdminResult.Invalid(started.Reason ?? "It could not be started.");
    }

    public async Task<(bool Forbidden, bool AutoReplace)> GetSettingsAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return (true, false);
        return (false, await store.GetAutoReplaceAsync(cancellationToken) ?? options.AutoReplace);
    }

    public async Task<LibraryUpgradeAdminResult> SetAutoReplaceAsync(bool autoReplace, CancellationToken cancellationToken)
    {
        if (!IsAdmin) return LibraryUpgradeAdminResult.ForbiddenResult();
        await store.SetAutoReplaceAsync(autoReplace, currentUser.UserId, clock.UtcNow, cancellationToken);
        await audit.WriteAsync(AuditActions.LibraryUpgradeSettingsChanged, AuditSubjectTypes.Storage, null,
            new { AutoReplace = autoReplace, ActorUserId = currentUser.UserId }, cancellationToken);
        return LibraryUpgradeAdminResult.Ok();
    }

    public async Task<(bool Forbidden, IReadOnlyList<LibraryUpgradeView> Upgrades)> ListAsync(CancellationToken cancellationToken)
    {
        if (!IsAdmin) return (true, []);
        return (false, await store.ListRecentAsync(100, cancellationToken));
    }

    public async Task<LibraryUpgradeAdminResult> DismissAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!IsAdmin) return LibraryUpgradeAdminResult.ForbiddenResult();
        var upgrade = await store.FindAsync(id, cancellationToken);
        if (upgrade is null) return LibraryUpgradeAdminResult.Missing();
        if (upgrade.State != LibraryUpgradeState.Proposed) return LibraryUpgradeAdminResult.Invalid("Only a proposal that has not started can be set aside.");
        upgrade.Dismiss(clock.UtcNow);
        await store.SaveAsync(cancellationToken);
        await audit.WriteAsync(AuditActions.LibraryUpgradeDismissed, AuditSubjectTypes.BookRequest, upgrade.RequestId.ToString(),
            new { UpgradeId = upgrade.Id, ActorUserId = currentUser.UserId }, cancellationToken);
        return LibraryUpgradeAdminResult.Ok();
    }

    private bool IsAdmin => currentUser.UserId is not null && administrator.IsAdministrator;
}
