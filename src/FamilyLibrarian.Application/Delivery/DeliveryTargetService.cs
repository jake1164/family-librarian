using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

/// <summary>
/// The commands and queries behind a user's Kindle delivery settings page.
/// </summary>
/// <remarks>
/// Ownership is enforced here, not at the endpoint, mirroring <c>UserWorkFeedbackService</c>:
/// every method resolves the caller from <see cref="ICurrentUser"/>, and a row
/// that belongs to someone else is never reachable through this service at all.
/// <para>
/// Beta scope keeps this to exactly one <see cref="DeliveryTargetProvider.CwaKindleEmail"/>
/// target per user. <see cref="DeliveryTarget"/> itself is not limited to one
/// per user -- nothing in this service exposes creating a second yet; see
/// docs/01-product-architecture-spec.md §15 for the future multi-target
/// direction.
/// </para>
/// </remarks>
public sealed class DeliveryTargetService(
    IDeliveryTargetRepository repository,
    IEnumerable<IEbookDeliveryProvider> deliveryProviders,
    ICurrentUser currentUser,
    IClock clock)
{
    private const string CwaProviderId = "cwa";

    public async Task<DeliveryTarget?> GetMyKindleTargetAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } userId
            ? await FindKindleTargetAsync(userId, cancellationToken)
            : null;

    /// <param name="expectedVersion">
    /// <see langword="null"/> when adding a Kindle address for the first time;
    /// the row's current <c>Version</c> when correcting it, read from a prior
    /// <see cref="GetMyKindleTargetAsync"/>. A stale or missing value where one
    /// was required answers <see cref="SetKindleTargetOutcome.Conflict"/>
    /// rather than silently overwriting a change the caller has not seen.
    /// </param>
    public async Task<SetKindleTargetResult> SetMyKindleAddressAsync(
        string address,
        uint? expectedVersion,
        bool sendByDefault,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return SetKindleTargetResult.Unauthenticated();
        }

        if (!TryNormalizeEmail(address, out var normalized, out var error))
        {
            return SetKindleTargetResult.Invalid(error);
        }

        var existing = await FindKindleTargetAsync(userId, cancellationToken);
        if (existing is null)
        {
            if (expectedVersion is not null)
            {
                // The client's expectedVersion came from a row that is gone now
                // (removed from another tab, say) -- not a blind create.
                return SetKindleTargetResult.Conflict();
            }

            var created = new DeliveryTarget(
                userId, DeliveryTargetProvider.CwaKindleEmail, "Kindle", normalized, clock.UtcNow);
            created.SetSendByDefault(sendByDefault, clock.UtcNow);
            repository.Add(created);
            await repository.SaveChangesAsync(cancellationToken);
            return SetKindleTargetResult.Success(created);
        }

        if (existing.Version != expectedVersion)
        {
            return SetKindleTargetResult.Conflict();
        }

        existing.UpdateAddress(normalized, clock.UtcNow);
        existing.SetSendByDefault(sendByDefault, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        return SetKindleTargetResult.Success(existing);
    }

    public async Task<SetKindleTargetResult> SetMyKindleEnabledAsync(
        bool enabled,
        uint expectedVersion,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return SetKindleTargetResult.Unauthenticated();
        }

        var existing = await FindKindleTargetAsync(userId, cancellationToken);
        if (existing is null)
        {
            return SetKindleTargetResult.NotFound();
        }

        if (existing.Version != expectedVersion)
        {
            return SetKindleTargetResult.Conflict();
        }

        existing.SetEnabled(enabled, clock.UtcNow);
        await repository.SaveChangesAsync(cancellationToken);
        return SetKindleTargetResult.Success(existing);
    }

    /// <summary>
    /// A login/connectivity check against the shared admin-configured e-reader
    /// service account -- not a real book send (the destination's send route
    /// has no dry-run). Available to any signed-in user, not gated on the
    /// caller having their own Kindle address configured, since it validates
    /// the shared infrastructure rather than anything user-specific.
    /// </summary>
    public async Task<TestKindleDeliveryResult> TestKindleDeliveryAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is null)
        {
            return new TestKindleDeliveryResult(false, "Sign in and try again.");
        }

        var provider = deliveryProviders.FirstOrDefault(candidate => candidate.Id == CwaProviderId);
        if (provider is null)
        {
            return new TestKindleDeliveryResult(false, "Kindle delivery is not available.");
        }

        var outcome = await provider.TestAsync(cancellationToken);
        return new TestKindleDeliveryResult(outcome.Succeeded, outcome.Message);
    }

    private async Task<DeliveryTarget?> FindKindleTargetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var targets = await repository.ListForUserAsync(userId, cancellationToken);
        return targets.FirstOrDefault(target => target.Provider == DeliveryTargetProvider.CwaKindleEmail);
    }

    private static bool TryNormalizeEmail(string? address, out string normalized, out string? error)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(address))
        {
            error = "Enter your Kindle email address.";
            return false;
        }

        try
        {
            normalized = new System.Net.Mail.MailAddress(address.Trim()).Address;
            error = null;
            return true;
        }
        catch (FormatException)
        {
            error = "Enter a valid email address.";
            return false;
        }
    }
}

public sealed record SetKindleTargetResult(SetKindleTargetOutcome Outcome, DeliveryTarget? Target, string? Error)
{
    public static SetKindleTargetResult Success(DeliveryTarget target) =>
        new(SetKindleTargetOutcome.Success, target, null);

    public static SetKindleTargetResult NotFound() =>
        new(SetKindleTargetOutcome.NotFound, null, null);

    public static SetKindleTargetResult Conflict() =>
        new(SetKindleTargetOutcome.Conflict, null, "This has changed since you loaded it. Reload and try again.");

    public static SetKindleTargetResult Invalid(string? error) =>
        new(SetKindleTargetOutcome.Invalid, null, error);

    public static SetKindleTargetResult Unauthenticated() =>
        new(SetKindleTargetOutcome.Unauthenticated, null, null);
}

public enum SetKindleTargetOutcome
{
    Success,
    NotFound,
    Conflict,
    Invalid,
    Unauthenticated
}

public sealed record TestKindleDeliveryResult(bool Succeeded, string Message);
