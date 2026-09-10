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
/// that does either. Request release, existing-book sends, explicit retries
/// and background recovery share <see cref="SubmitAsync"/>.
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
    private const int MaxAttempts = DeliveryRetryPolicy.MaxAttempts;
    private static readonly TimeSpan SubmissionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(5);
    private const string UnknownSubmissionMessage =
        "The send was interrupted and may already have been accepted. Check your Kindle before resending; another send may create a duplicate.";



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

        var pending = new List<DeliveryAttempt>();
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
                // No attempt yet: recovery can release this intent once the
                // target is enabled again and the recipient is eligible.
                continue;
            }

            var attempt = new DeliveryAttempt(
                request.Id, participant.UserId, target.Id, ResolveProviderId(target.Provider),
                externalBookId, normalizedBookFormat, convert: false, attemptNumber: 1, atUtc, workTitle);
            if (target.UserId == participant.UserId && await repository.TryAddAsync(attempt, cancellationToken))
            {
                pending.Add(attempt);
            }
        }

        // All recipients are durable before the first external side effect.
        foreach (var attempt in pending)
        {
            await TrySubmitAsync(attempt, cancellationToken);
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
        // existing-library lookup, so this path assumes CWA's library-wide
        // auto-convert target format (epub, CWA's own default) rather than
        // this book's real stored format -- a deliberate beta simplification,
        // unlike the release path above, which knows the real uploaded
        // format. Critically this must be sent with convert=false: CWA's
        // send_selected does NOT treat convert=1 as "convert to this format"
        // -- it hardcodes the SOURCE format as mobi (convert=2 hardcodes
        // azw3) and converts to book_format, so requesting convert=true here
        // asked CWA to convert a mobi copy that was never uploaded and always
        // failed with "mobi format not found for book id: ...". convert=false
        // sends the assumed format directly, the same way the release path
        // above does.
        var attempt = new DeliveryAttempt(
            requestId: null, userId, target.Id, ResolveProviderId(target.Provider),
            externalBookId, bookFormat: "epub", convert: false, attemptNumber: 1, clock.UtcNow, work?.CanonicalTitle);
        await repository.TryAddAsync(attempt, cancellationToken);

        await TrySubmitAsync(attempt, cancellationToken);

        return attempt.Status == DeliveryAttemptStatus.Submitted
            ? SendExistingBookResult.Success(attempt)
            : SendExistingBookResult.Failed(attempt);
    }

    /// <summary>
    /// Finds every eligible retryable-failed attempt and submits a fresh
    /// attempt row for it. Only the latest attempt for a given
    /// delivery identity is ever retried, so a superseded failure never
    /// fires twice.
    /// </summary>
    /// <returns>The number of attempts retried.</returns>
    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken)
    {
        var oldestEligibleCompletion = clock.UtcNow - DeliveryRetryPolicy.Cooldown(1);
        var candidates = await repository.ListRetryableFailedAsync(
            oldestEligibleCompletion, MaxAttempts, cancellationToken);

        var retried = 0;
        foreach (var failed in candidates)
        {
            var cooldown = DeliveryRetryPolicy.Cooldown(failed.AttemptNumber);
            if (failed.CompletedAtUtc is not { } completedAtUtc || clock.UtcNow - completedAtUtc < cooldown)
            {
                continue;
            }

            var result = await CreateRetryAsync(failed, cancellationToken);
            if (result.Created) retried++;
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
    public async Task<RetryDeliveryResult> RetryAsync(Guid attemptId, CancellationToken cancellationToken,
        bool confirmPossibleDuplicate = false)
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

        if (attempt.Status == DeliveryAttemptStatus.SubmissionUnknown && !confirmPossibleDuplicate)
        {
            return RetryDeliveryResult.DuplicateConfirmationRequired();
        }

        var eligible = DeliveryRetryPolicy.CanRetry(attempt.Status, attempt.ConfirmationStatus);
        if (!eligible)
        {
            return RetryDeliveryResult.NotFailed();
        }

        var retry = await CreateRetryAsync(attempt, cancellationToken);
        return RetryDeliveryResult.Success(retry.Attempt);
    }

    /// <summary>
    /// The admin queue's retry action. Ownership is not checked here -- the
    /// endpoint calling this is already <c>RequireAuthorization("Admin")</c>.
    /// </summary>
    public async Task<bool> AdminRetryAsync(Guid attemptId, CancellationToken cancellationToken,
        bool confirmPossibleDuplicate = false)
    {
        var attempt = await repository.FindAsync(attemptId, cancellationToken);
        if (attempt is null || !DeliveryRetryPolicy.CanRetry(attempt.Status, attempt.ConfirmationStatus) ||
            (attempt.Status == DeliveryAttemptStatus.SubmissionUnknown && !confirmPossibleDuplicate))
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

    public async Task<IReadOnlyList<PersonalDeliveryAttemptView>?> ListMineAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return null;
        var attempts = await repository.ListForUserAsync(userId, cancellationToken);
        var latest = attempts.GroupBy(attempt => attempt.DeliveryId)
            .ToDictionary(group => group.Key, group => group.MaxBy(attempt => attempt.AttemptNumber)!.Id);
        return attempts.Select(attempt => PersonalDeliveryAttemptView.From(attempt, latest[attempt.DeliveryId])).ToArray();
    }

    public async Task<PersonalDeliveryAttemptView?> GetMineAsync(Guid id, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return null;
        var attempt = await repository.FindAsync(id, cancellationToken);
        if (attempt is null || attempt.UserId != userId) return null;
        var latest = await repository.FindLatestAsync(attempt.DeliveryId, cancellationToken);
        return PersonalDeliveryAttemptView.From(attempt, latest?.Id ?? attempt.Id);
    }

    /// <summary>Recover durable work using verified library state, never by repeating an uncertain send.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var ready in await repository.ListUnreleasedAsync(cancellationToken))
        {
            await ReleaseForRequestFormatAsync(ready.Request, ready.ExternalBookId, ready.BookFormat,
                clock.UtcNow, cancellationToken, ready.WorkTitle);
        }

        foreach (var attempt in await repository.ListUnfinishedAsync(cancellationToken))
        {
            if (attempt.Status == DeliveryAttemptStatus.Pending)
            {
                // Legacy duplicate releases may leave an older Pending row
                // behind a completed successor. Never revive superseded work.
                var latest = await repository.FindLatestAsync(attempt.DeliveryId, cancellationToken);
                if (latest is not null && latest.Id != attempt.Id)
                {
                    if (await repository.TryTransitionAsync(attempt, DeliveryAttemptStatus.Cancelled,
                        clock.UtcNow, cancellationToken, "Superseded by a later delivery attempt."))
                        await WriteAuditAsync(attempt, succeeded: false, cancellationToken);
                }
                else
                {
                    await TrySubmitAsync(attempt, cancellationToken);
                }
            }
            else if (attempt.StartedAtUtc is not { } started || clock.UtcNow - started >= InterruptedAfter)
            {
                if (await repository.TryTransitionAsync(attempt, DeliveryAttemptStatus.SubmissionUnknown,
                    clock.UtcNow, cancellationToken, UnknownSubmissionMessage))
                    await WriteAuditAsync(attempt, succeeded: false, cancellationToken);
            }
        }
    }

    private async Task<(DeliveryAttempt Attempt, bool Created)> CreateRetryAsync(
        DeliveryAttempt failed, CancellationToken cancellationToken)
    {
        var latest = await repository.FindLatestAsync(failed.DeliveryId, cancellationToken);
        if (latest is not null && latest.Id != failed.Id) return (latest, false);

        var retry = new DeliveryAttempt(
            failed.RequestId, failed.UserId, failed.DeliveryTargetId, failed.Provider,
            failed.ExternalBookId, failed.BookFormat, failed.Convert, failed.AttemptNumber + 1, clock.UtcNow,
            failed.BookTitle, failed.DeliveryId);
        if (!await repository.TryAddAsync(retry, cancellationToken))
            return ((await repository.FindLatestAsync(failed.DeliveryId, cancellationToken))!, false);

        await TrySubmitAsync(retry, cancellationToken);
        return (retry, true);
    }

    private async Task TrySubmitAsync(DeliveryAttempt attempt, CancellationToken cancellationToken)
    {
        try
        {
            await SubmitAsync(attempt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The durable Pending/Submitting row is recovered by the next sweep.
            // One participant's storage failure must not abandon the others.
            await WriteAuditAsync(attempt, succeeded: false, cancellationToken);
        }
    }

    private async Task SubmitAsync(DeliveryAttempt attempt, CancellationToken cancellationToken)
    {
        if (attempt.Status != DeliveryAttemptStatus.Pending) return;

        var target = await repository.GetEligibleTargetAsync(attempt, cancellationToken);
        if (target is null)
        {
            if (await repository.TryTransitionAsync(attempt, DeliveryAttemptStatus.Cancelled, clock.UtcNow,
                cancellationToken, "Delivery cancelled: the target or recipient is disabled, or the request was withdrawn."))
                await WriteAuditAsync(attempt, succeeded: false, cancellationToken);
            return;
        }

        // xmin arbitrates multiple workers loading the same Pending row. Only
        // the caller whose durable claim succeeds may contact the provider.
        if (!await repository.TryTransitionAsync(attempt, DeliveryAttemptStatus.Submitting,
            clock.UtcNow, cancellationToken)) return;

        var provider = deliveryProviders.FirstOrDefault(candidate => candidate.Id == attempt.Provider);
        EbookDeliveryOutcome outcome;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(SubmissionTimeout);
            try
            {
                outcome = provider is null
                    ? EbookDeliveryOutcome.NotConfigured("No delivery provider is registered for this target.")
                    : await provider.DeliverAsync(attempt.ExternalBookId, attempt.BookFormat, attempt.Convert,
                        target.Address, timeout.Token).WaitAsync(timeout.Token);
            }
            catch (Exception)
            {
                // Includes request cancellation and timeout after dispatch. A
                // retry could duplicate an accepted send, so never guess Failed.
                outcome = EbookDeliveryOutcome.Unknown(UnknownSubmissionMessage);
            }
        }

        var status = outcome.Status switch
        {
            EbookDeliveryStatus.Delivered => DeliveryAttemptStatus.Submitted,
            EbookDeliveryStatus.SubmissionUnknown => DeliveryAttemptStatus.SubmissionUnknown,
            _ => DeliveryAttemptStatus.Failed
        };

        // Persist the observed result even if the initiating browser went away.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (!await repository.TryTransitionAsync(attempt, status, clock.UtcNow, completion.Token,
            outcome.Message, retryable: outcome.Status == EbookDeliveryStatus.TransportFailure)) return;
        await WriteAuditAsync(attempt, outcome.Succeeded, completion.Token);

        if (attempt.Status == DeliveryAttemptStatus.Submitted)
        {
            try
            {
                await notifications.RecordKindleDeliverySubmittedAsync(
                    attempt.UserId, attempt.Id, attempt.BookTitle, completion.Token);
            }
            catch (Exception)
            {
                // A notification failure cannot change the already durable send outcome.
            }
        }
    }

    private async Task WriteAuditAsync(DeliveryAttempt attempt, bool succeeded, CancellationToken cancellationToken)
    {
        try
        {
            await audit.WriteAsync(
                succeeded ? AuditActions.DeliveryAttemptSubmitted : AuditActions.DeliveryAttemptFailed,
                AuditSubjectTypes.DeliveryAttempt, attempt.Id.ToString(),
                new { attempt.Id, attempt.DeliveryId, attempt.RequestId, attempt.UserId,
                    attempt.ExternalBookId, attempt.Provider, attempt.AttemptNumber, Status = attempt.Status.ToString() },
                cancellationToken);
        }
        catch (Exception)
        {
            // The delivery row remains authoritative if the audit store is unavailable.
        }
    }

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

    public static RetryDeliveryResult DuplicateConfirmationRequired() =>
        new(RetryDeliveryOutcome.DuplicateConfirmationRequired, null);

    public static RetryDeliveryResult Unauthenticated() => new(RetryDeliveryOutcome.Unauthenticated, null);
}

public enum RetryDeliveryOutcome
{
    DuplicateConfirmationRequired,
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
