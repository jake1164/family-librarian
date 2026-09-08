using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
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
    IClock clock)
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
        CancellationToken cancellationToken)
    {
        var existing = await repository.ListForRequestAsync(request.Id, cancellationToken);
        var alreadyReleasedUserIds = existing.Select(attempt => attempt.UserId).ToHashSet();

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
                externalBookId, bookFormat, convert: false, attemptNumber: 1, atUtc);
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

        // The original upload format isn't tracked anywhere reachable from an
        // existing-library lookup, so this path always asks CWA to convert to
        // epub on the fly -- a deliberate beta simplification, unlike the
        // release path above, which knows the real uploaded format.
        var attempt = new DeliveryAttempt(
            requestId: null, userId, target.Id, ResolveProviderId(target.Provider),
            externalBookId, bookFormat: "epub", convert: true, attemptNumber: 1, clock.UtcNow);
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

            var retry = new DeliveryAttempt(
                failed.RequestId, failed.UserId, failed.DeliveryTargetId, failed.Provider,
                failed.ExternalBookId, failed.BookFormat, failed.Convert, failed.AttemptNumber + 1, clock.UtcNow);
            repository.Add(retry);
            await repository.SaveChangesAsync(cancellationToken);

            await SubmitAsync(retry, cancellationToken);
            retried++;
        }

        return retried;
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
