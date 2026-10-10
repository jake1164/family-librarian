using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Owns every claim rule for HUMAN-ACQ-1's provider human-interaction sessions, so
/// the Matrix-magic-link path (<c>ProviderInteractionLinkService</c>) and the
/// in-app path (<c>ProviderInteractionService</c>) cannot diverge. See
/// <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> §2 for why claims live in
/// their own table: two concurrent claimants race on the store's own
/// insert-or-fail atomicity, not on anything decided here.
/// </summary>
public sealed class ProviderInteractionClaimService(
    IProviderAcquisitionJobStore jobs,
    IProviderInteractionClaimStore claims,
    IUserAccountStore accounts,
    RemoteViewSessionRegistry viewSessions,
    ProviderInteractionAlertOptions options,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>
    /// Attempts to claim a job for <paramref name="userId"/>. Idempotent for the
    /// current holder (<see cref="ClaimOutcomeKind.AlreadyYours"/>); reclaims a
    /// lapsed claim automatically; never overwrites a live one.
    /// </summary>
    public async Task<ClaimOutcome> TryClaimForUserAsync(
        Guid jobId, Guid userId, ProviderInteractionClaimChannel channel, Guid? alertId,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);
        if (!IsClaimable(job))
        {
            return ClaimOutcome.NotClaimable();
        }

        var existing = await claims.FindAsync(jobId, cancellationToken);
        if (existing is not null)
        {
            if (existing.ClaimedByUserId == userId)
            {
                return ClaimOutcome.AlreadyYours(existing);
            }

            if (IsLeaseLive(existing))
            {
                return ClaimOutcome.HeldByOther(await DisplayNameAsync(existing.ClaimedByUserId, cancellationToken));
            }

            claims.Remove(existing);
            await claims.SaveChangesAsync(cancellationToken);
        }

        var claim = new ProviderInteractionClaim(jobId, job!.ExternalProviderId, userId, channel, alertId, clock.UtcNow);
        if (!await claims.TryInsertAsync(claim, cancellationToken))
        {
            // Lost the race to another claimant between the check above and the
            // insert -- report who actually won, rather than looping.
            var winner = await claims.FindAsync(jobId, cancellationToken);
            if (winner is null)
            {
                return ClaimOutcome.NotClaimable();
            }

            return winner.ClaimedByUserId == userId
                ? ClaimOutcome.AlreadyYours(winner)
                : ClaimOutcome.HeldByOther(await DisplayNameAsync(winner.ClaimedByUserId, cancellationToken));
        }

        // Matrix claims are provisional until their provider alert is won.
        // The link service records the audit only after that compare-and-save.
        if (channel != ProviderInteractionClaimChannel.MatrixLink)
        {
            await audit.WriteAsync(
                AuditActions.ProviderInteractionClaimed, AuditSubjectTypes.ProviderInteraction, jobId.ToString(),
                new { JobId = jobId, UserId = userId, Channel = channel.ToString() }, cancellationToken);
        }

        return ClaimOutcome.Claimed(claim);
    }

    /// <summary>Always replaces an existing claim, regardless of lease state — an explicit, audited override.</summary>
    public async Task<ProviderInteractionClaim> TakeOverAsync(Guid jobId, Guid userId, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken) ??
            throw new InvalidOperationException($"Job {jobId} does not exist.");

        if (!IsClaimable(job))
        {
            throw new InvalidOperationException($"Job {jobId} is not waiting for interaction.");
        }

        if (!await viewSessions.RequestCloseAndWaitAsync(jobId, cancellationToken))
        {
            throw new TimeoutException($"The current viewer for job {jobId} did not disconnect.");
        }

        var existing = await claims.FindAsync(jobId, cancellationToken);
        var previousHolderUserId = existing?.ClaimedByUserId;
        if (existing is not null)
        {
            claims.Remove(existing);
            await claims.SaveChangesAsync(cancellationToken);
        }

        var claim = new ProviderInteractionClaim(
            jobId, job.ExternalProviderId, userId, ProviderInteractionClaimChannel.TakeOver, alertId: null, clock.UtcNow);
        if (!await claims.TryInsertAsync(claim, cancellationToken))
        {
            // A fresh concurrent claim landed between the delete above and this
            // insert -- exceedingly unlikely for an explicit take-over, but the
            // same retry-once discipline as TryClaimForUserAsync applies.
            var stale = await claims.FindAsync(jobId, cancellationToken);
            if (stale is not null)
            {
                claims.Remove(stale);
                await claims.SaveChangesAsync(cancellationToken);
            }

            if (!await claims.TryInsertAsync(claim, cancellationToken))
            {
                throw new InvalidOperationException($"Could not take over job {jobId}: another claim keeps winning.");
            }
        }

        await audit.WriteAsync(
            AuditActions.ProviderInteractionTakenOver, AuditSubjectTypes.ProviderInteraction, jobId.ToString(),
            new { JobId = jobId, UserId = userId, PreviousHolderUserId = previousHolderUserId }, cancellationToken);

        return claim;
    }

    /// <summary>Whether <paramref name="userId"/> currently holds a live claim on this job.</summary>
    public async Task<bool> IsHolderAsync(Guid jobId, Guid userId, CancellationToken cancellationToken)
    {
        var claim = await claims.FindAsync(jobId, cancellationToken);
        if (claim is null || claim.ClaimedByUserId != userId || !IsLeaseLive(claim))
        {
            return false;
        }

        var account = await accounts.FindAsync(userId, cancellationToken);
        return account is { IsAdmin: true } && UserStatuses.CanSignIn(account.Status);
    }

    public Task ReleaseProvisionalAlertClaimAsync(Guid jobId, Guid userId, Guid alertId, CancellationToken cancellationToken) =>
        claims.ReleaseIfOwnedAsync(jobId, userId, alertId, cancellationToken);

    /// <summary>Deletes <paramref name="claim"/> if its lease has lapsed. Returns whether it was removed.</summary>
    public async Task<bool> ReleaseIfLapsedAsync(ProviderInteractionClaim claim, CancellationToken cancellationToken)
    {
        if (IsLeaseLive(claim))
        {
            return false;
        }

        claims.Remove(claim);
        await claims.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Unconditionally releases a job's claim, if any — used when a job leaves Waiting.</summary>
    public async Task ReleaseForJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var claim = await claims.FindAsync(jobId, cancellationToken);
        if (claim is not null)
        {
            claims.Remove(claim);
            await claims.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Whether <paramref name="claim"/>'s lease is still live right now — exposed for read-only views (e.g. the admin queue).</summary>
    public bool IsLeaseLive(ProviderInteractionClaim claim) =>
        claim.IsLeaseLive(clock.UtcNow, options.ClaimLease, viewSessions.IsActive(claim.JobId));

    private bool IsClaimable(ProviderAcquisitionJob? job) =>
        job is not null &&
        job.LifecycleState == ProviderAcquisitionJobLifecycleState.Waiting &&
        !string.IsNullOrWhiteSpace(job.InteractionType) &&
        (job.InteractionExpiresAtUtc is not { } expiresAt || expiresAt > clock.UtcNow);

    private async Task<string> DisplayNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(userId, cancellationToken);
        return account?.DisplayName ?? "another administrator";
    }
}

public enum ClaimOutcomeKind
{
    Claimed,
    AlreadyYours,
    HeldByOther,
    NotClaimable
}

public sealed record ClaimOutcome(ClaimOutcomeKind Kind, ProviderInteractionClaim? Claim, string? HeldByDisplayName)
{
    public static ClaimOutcome Claimed(ProviderInteractionClaim claim) => new(ClaimOutcomeKind.Claimed, claim, null);

    public static ClaimOutcome AlreadyYours(ProviderInteractionClaim claim) => new(ClaimOutcomeKind.AlreadyYours, claim, null);

    public static ClaimOutcome HeldByOther(string displayName) => new(ClaimOutcomeKind.HeldByOther, null, displayName);

    public static ClaimOutcome NotClaimable() => new(ClaimOutcomeKind.NotClaimable, null, null);
}
