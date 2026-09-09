using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Notifications;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Delivery;

/// <summary>
/// Creates and submits <see cref="DeliveryAttempt"/> rows -- the only place
/// that does either. Three entry points share <see cref="SubmitAsync"/>: the
/// automatic release triggered from <c>CwaPublishingService</c> when a
/// requested ebook becomes available, the existing-book "Send to Kindle" fast
/// path, and the background retry sweep.
/// </summary>
public sealed class DeliveryAttemptService(
    IDeliveryAttemptRepository repository,
    IDeliveryTargetRepository targetRepository,
    IEnumerable<IEbookDeliveryProvider> deliveryProviders,
    IEnumerable<IOwnedLibraryProvider> ownedLibraryProviders,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock,
    ICatalogRepository catalogRepository,
    NotificationService notifications)
{
    /// <summary>Beta retry policy (beta plan §20) -- a fixed step schedule, not exponential backoff.</summary>
    private const int MaxAttempts = 3;

    private static readonly TimeSpan[] RetryCooldowns = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// Releases a Pending delivery attempt for every active participant who
    /// asked for Kindle delivery on this request's ebook format. Safe to call
    /// more than once for the same request -- a participant who already has a
    /// row is skipped, so this is a no-op the second time a caller invokes it.
    /// </summary>
    public async Task ReleaseForRequestFormatAsync(
        BookRequest request,
        string externalBookId,
        string bookFormat,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken,
        string? workTitle = null)
    {
        var existing = await repository.ListForRequestAsync(request.Id, cancellationToken);
        var alreadyReleasedUserIds = existing.Select(attempt => attempt.UserId).ToHashSet();

        // MediaAsset.Format carries a leading dot (Path.GetExtension's shape,
        // e.g. ".epub") for its own extension-policy purposes, but CWA's
        // /send_selected route matches this against Calibre's stored format
        // token, which is always bare and uppercase (e.g. "EPUB") -- an
        // unstripped dot never matches, and CWA reports that as a generic
        // "could not be read" file error rather than a format mismatch.
        var normalizedBookFormat = bookFormat.TrimStart('.');

        foreach (var participant in request.Participants)
        {
            if (participant.WithdrawnAtUtc is not null ||
                !participant.WantsEbook ||
                participant.DeliveryTargetId is not { } deliveryTargetId ||
                alreadyReleasedUserIds.Contains(participant.UserId))
            {
                continue;
            }

            var target = await targetRepository.FindAsync(deliveryTargetId, cancellationToken);
            if (target is null || !target.IsEnabled)
            {
                // Nothing to retry from here -- the user must re-enable/reconfigure
                // and use the existing-book fast path if they want it later.
                continue;
            }

            var attempt = new DeliveryAttempt(
                request.Id, participant.UserId, target.Id, ResolveProviderId(target.Provider),
                externalBookId, normalizedBookFormat, convert: false, attemptNumber: 1, atUtc, workTitle);
            repository.Add(attempt);
            await repository.SaveChangesAsync(cancellationToken);

            await SubmitAsync(attempt, cancellationToken);
        }
    }

    /// <summary>
    /// The existing-book "already in CWA -&gt; Send to Kindle" fast path (beta
    /// plan §6/§27). Resolves the CWA book id itself through
    /// <see cref="IOwnedLibraryProvider"/> rather than trusting a
    /// client-supplied id.
    /// </summary>
    public async Task<SendExistingBookResult> SendExistingBookAsync(Guid workId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return SendExistingBookResult.Unauthenticated();
        }

        var targets = await targetRepository.ListForUserAsync(userId, cancellationToken);
        var target = targets.FirstOrDefault(
            candidate => candidate.Provider == DeliveryTargetProvider.CwaKindleEmail && candidate.IsEnabled);
        if (target is null)
        {
            return SendExistingBookResult.TargetNotConfigured();
        }

        string? externalBookId = null;
        foreach (var provider in ownedLibraryProviders)
        {
            var matches = await provider.FindOwnedMatchesAsync(workId, RequestMediaType.Ebook, cancellationToken);
            var owned = matches.FirstOrDefault(option => option.OptionKind == OptionKind.Owned);
            if (owned is not null)
            {
                externalBookId = owned.ProviderResultId;
                break;
            }
        }

        if (externalBookId is null)
        {
            return SendExistingBookResult.NotOwned();
        }

        var work = await catalogRepository.GetWorkAsync(workId, cancellationToken);

        // The original upload format isn't tracked anywhere reachable from an
        // existing-library lookup, so this path always asks CWA to convert to
        // epub on the fly -- a deliberate beta simplification, unlike the
        // release path above, which knows the real uploaded format.
        var attempt = new DeliveryAttempt(
            requestId: null, userId, target.Id, ResolveProviderId(target.Provider),
            externalBookId, bookFormat: "epub", convert: true, attemptNumber: 1, clock.UtcNow, work?.CanonicalTitle);
        repository.Add(attempt);
        await repository.SaveChangesAsync(cancellationToken);

        await SubmitAsync(attempt, cancellationToken);

        return attempt.Status == DeliveryAttemptStatus.Submitted
            ? SendExistingBookResult.Success(attempt)
            : SendExistingBookResult.Failed(attempt);
    }

    /// <summary>
    /// Finds every eligible retryable-failed attempt and submits a fresh
    /// attempt row for it. Only the latest attempt for a given
    /// (RequestId, UserId) pair is ever retried, so a superseded failure never
    /// fires twice.
    /// </summary>
    /// <returns>The number of attempts retried.</returns>
    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken)
    {
        var oldestEligibleCompletion = clock.UtcNow - RetryCooldowns[0];
        var candidates = await repository.ListRetryableFailedAsync(
            oldestEligibleCompletion, MaxAttempts, cancellationToken);

        var latestPerPair = candidates
            .GroupBy(attempt => (attempt.RequestId, attempt.UserId))
            .Select(group => group.OrderByDescending(attempt => attempt.AttemptNumber).First());

        var retried = 0;
        foreach (var failed in latestPerPair)
        {
            var cooldown = RetryCooldowns[Math.Min(failed.AttemptNumber - 1, RetryCooldowns.Length - 1)];
            if (failed.CompletedAtUtc is not { } completedAtUtc || clock.UtcNow - completedAtUtc < cooldown)
            {
                continue;
            }

            await CreateRetryAsync(failed, cancellationToken);
            retried++;
        }

        return retried;
    }

    /// <summary>
    /// A user-initiated retry of their own failed attempt -- unlike
    /// <see cref="RetryFailedAsync"/>, this bypasses the automatic sweep's
    /// <see cref="DeliveryAttempt.IsRetryable"/>/cooldown gating (beta plan
    /// §19): the user may have just fixed the underlying problem (e.g.
    /// reconfigured their Kindle address after a <see cref="EbookDeliveryStatus.NotConfigured"/>
    /// failure), and an explicit request to resend should not wait for a
    /// cooldown meant for background retries.
    /// <para>
    /// Also allowed for a <see cref="DeliveryAttemptStatus.Submitted"/> attempt
    /// the user has <see cref="DeliveryAttempt.ReportMissing"/> -- KINDLE-7:
    /// CWA accepted the send, so the row itself never moved to <c>Failed</c>,
    /// but "it never arrived" is exactly the same "send it again" intent.
    /// </para>
    /// </summary>
    public async Task<RetryDeliveryResult> RetryAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return RetryDeliveryResult.Unauthenticated();
        }

        var attempt = await repository.FindAsync(attemptId, cancellationToken);
        if (attempt is null || attempt.UserId != userId)
        {
            return RetryDeliveryResult.NotFound();
        }

        var eligible = attempt.Status == DeliveryAttemptStatus.Failed ||
            (attempt.Status == DeliveryAttemptStatus.Submitted &&
                attempt.ConfirmationStatus == DeliveryConfirmationStatus.ReportedMissing);
        if (!eligible)
        {
            return RetryDeliveryResult.NotFailed();
        }

        var retry = await CreateRetryAsync(attempt, cancellationToken);
        return RetryDeliveryResult.Success(retry);
    }

    /// <summary>
    /// The admin queue's retry action. Ownership is not checked here -- the
    /// endpoint calling this is already <c>RequireAuthorization("Admin")</c>.
    /// </summary>
    public async Task<bool> AdminRetryAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await repository.FindAsync(attemptId, cancellationToken);
        if (attempt is null || attempt.Status != DeliveryAttemptStatus.Failed)
        {
            return false;
        }

        await CreateRetryAsync(attempt, cancellationToken);
        return true;
    }

    /// <summary>
    /// KINDLE-7: the user confirms a <see cref="DeliveryAttemptStatus.Submitted"/>
    /// attempt actually arrived on their Kindle.
    /// </summary>
    public Task<ConfirmDeliveryResult> ConfirmReceivedAsync(Guid attemptId, CancellationToken cancellationToken) =>
        RecordConfirmationAsync(
            attemptId, AuditActions.DeliveryAttemptConfirmed, attempt => attempt.ConfirmReceived(clock.UtcNow),
            cancellationToken);

    /// <summary>
    /// KINDLE-7: the user reports that a <see cref="DeliveryAttemptStatus.Submitted"/>
    /// attempt never arrived. Does not itself retry -- see the widened
    /// eligibility on <see cref="RetryAsync"/>.
    /// </summary>
    public Task<ConfirmDeliveryResult> ReportMissingAsync(Guid attemptId, CancellationToken cancellationToken) =>
        RecordConfirmationAsync(
            attemptId, AuditActions.DeliveryAttemptReportedMissing, attempt => attempt.ReportMissing(clock.UtcNow),
            cancellationToken);

    private async Task<ConfirmDeliveryResult> RecordConfirmationAsync(
        Guid attemptId, string auditAction, Action<DeliveryAttempt> apply, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return ConfirmDeliveryResult.Unauthenticated();
        }

        var attempt = await repository.FindAsync(attemptId, cancellationToken);
        if (attempt is null || attempt.UserId != userId)
        {
            return ConfirmDeliveryResult.NotFound();
        }

        if (attempt.Status != DeliveryAttemptStatus.Submitted)
        {
            return ConfirmDeliveryResult.NotSubmitted();
        }

        apply(attempt);
        await repository.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            auditAction, AuditSubjectTypes.DeliveryAttempt, attempt.Id.ToString(),
            new { attempt.Id, attempt.RequestId, attempt.UserId }, cancellationToken);

        return ConfirmDeliveryResult.Success(attempt);
    }

    private async Task<DeliveryAttempt> CreateRetryAsync(DeliveryAttempt failed, CancellationToken cancellationToken)
    {
        var retry = new DeliveryAttempt(
            failed.RequestId, failed.UserId, failed.DeliveryTargetId, failed.Provider,
            failed.ExternalBookId, failed.BookFormat, failed.Convert, failed.AttemptNumber + 1, clock.UtcNow,
            failed.BookTitle);
        repository.Add(retry);
        await repository.SaveChangesAsync(cancellationToken);

        await SubmitAsync(retry, cancellationToken);
        return retry;
    }

    private async Task SubmitAsync(DeliveryAttempt attempt, CancellationToken cancellationToken)
    {
        var provider = deliveryProviders.FirstOrDefault(candidate => candidate.Id == attempt.Provider);
        if (provider is null)
        {
            attempt.TransitionTo(
                DeliveryAttemptStatus.Submitting, clock.UtcNow);
            attempt.TransitionTo(
                DeliveryAttemptStatus.Failed, clock.UtcNow, "No delivery provider is registered for this target.");
            await repository.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(attempt, succeeded: false, cancellationToken);
            return;
        }

        var target = await targetRepository.FindAsync(attempt.DeliveryTargetId, cancellationToken);
        if (target is null)
        {
            attempt.TransitionTo(DeliveryAttemptStatus.Submitting, clock.UtcNow);
            attempt.TransitionTo(DeliveryAttemptStatus.Failed, clock.UtcNow, "The delivery target no longer exists.");
            await repository.SaveChangesAsync(cancellationToken);
            await WriteAuditAsync(attempt, succeeded: false, cancellationToken);
            return;
        }

        attempt.TransitionTo(DeliveryAttemptStatus.Submitting, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);

        var outcome = await provider.DeliverAsync(
            attempt.ExternalBookId, attempt.BookFormat, attempt.Convert, target.Address, cancellationToken);

        switch (outcome.Status)
        {
            case EbookDeliveryStatus.Delivered:
                attempt.TransitionTo(DeliveryAttemptStatus.Submitted, clock.UtcNow);
                break;
            case EbookDeliveryStatus.TransportFailure:
                attempt.TransitionTo(DeliveryAttemptStatus.Failed, clock.UtcNow, outcome.Message, retryable: true);
                break;
            default:
                attempt.TransitionTo(DeliveryAttemptStatus.Failed, clock.UtcNow, outcome.Message, retryable: false);
                break;
        }

        await repository.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(attempt, outcome.Succeeded, cancellationToken);

        if (attempt.Status == DeliveryAttemptStatus.Submitted)
        {
            // Best-effort, same posture as CwaPublishingService's own delivery
            // release -- a notification-write failure must never make an
            // otherwise-successful Kindle send look like it failed.
            try
            {
                await notifications.RecordKindleDeliverySubmittedAsync(
                    attempt.UserId, attempt.Id, attempt.BookTitle, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await audit.WriteAsync(
                    AuditActions.DeliveryAttemptSubmitted, AuditSubjectTypes.DeliveryAttempt, attempt.Id.ToString(),
                    new { attempt.Id, NotificationFailed = true, exception.Message }, cancellationToken);
            }
        }
    }

    private Task WriteAuditAsync(DeliveryAttempt attempt, bool succeeded, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            succeeded ? AuditActions.DeliveryAttemptSubmitted : AuditActions.DeliveryAttemptFailed,
            AuditSubjectTypes.DeliveryAttempt,
            attempt.Id.ToString(),
            new
            {
                attempt.Id,
                attempt.RequestId,
                attempt.UserId,
                attempt.ExternalBookId,
                attempt.Provider,
                attempt.AttemptNumber
            },
            cancellationToken);

    private static string ResolveProviderId(DeliveryTargetProvider provider) => provider switch
    {
        DeliveryTargetProvider.CwaKindleEmail => "cwa",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown delivery target provider.")
    };
}

public sealed record SendExistingBookResult(SendExistingBookOutcome Outcome, DeliveryAttempt? Attempt, string? Error)
{
    public static SendExistingBookResult Success(DeliveryAttempt attempt) =>
        new(SendExistingBookOutcome.Success, attempt, null);

    public static SendExistingBookResult Failed(DeliveryAttempt attempt) =>
        new(SendExistingBookOutcome.Failed, attempt, attempt.FailureReason);

    public static SendExistingBookResult NotOwned() =>
        new(SendExistingBookOutcome.NotOwned, null, "This book was not found in the library.");

    public static SendExistingBookResult TargetNotConfigured() =>
        new(SendExistingBookOutcome.TargetNotConfigured, null, "Kindle delivery is not configured.");

    public static SendExistingBookResult Unauthenticated() =>
        new(SendExistingBookOutcome.Unauthenticated, null, null);
}

public enum SendExistingBookOutcome
{
    Success,
    Failed,
    NotOwned,
    TargetNotConfigured,
    Unauthenticated
}

public sealed record RetryDeliveryResult(RetryDeliveryOutcome Outcome, DeliveryAttempt? Attempt)
{
    public static RetryDeliveryResult Success(DeliveryAttempt attempt) => new(RetryDeliveryOutcome.Success, attempt);

    public static RetryDeliveryResult NotFound() => new(RetryDeliveryOutcome.NotFound, null);

    public static RetryDeliveryResult NotFailed() => new(RetryDeliveryOutcome.NotFailed, null);

    public static RetryDeliveryResult Unauthenticated() => new(RetryDeliveryOutcome.Unauthenticated, null);
}

public enum RetryDeliveryOutcome
{
    Success,
    NotFound,
    NotFailed,
    Unauthenticated
}

/// <summary>KINDLE-7: the result of <c>ConfirmReceivedAsync</c>/<c>ReportMissingAsync</c>.</summary>
public sealed record ConfirmDeliveryResult(ConfirmDeliveryOutcome Outcome, DeliveryAttempt? Attempt)
{
    public static ConfirmDeliveryResult Success(DeliveryAttempt attempt) =>
        new(ConfirmDeliveryOutcome.Success, attempt);

    public static ConfirmDeliveryResult NotFound() => new(ConfirmDeliveryOutcome.NotFound, null);

    public static ConfirmDeliveryResult NotSubmitted() => new(ConfirmDeliveryOutcome.NotSubmitted, null);

    public static ConfirmDeliveryResult Unauthenticated() => new(ConfirmDeliveryOutcome.Unauthenticated, null);
}

public enum ConfirmDeliveryOutcome
{
    Success,
    NotFound,
    NotSubmitted,
    Unauthenticated
}
