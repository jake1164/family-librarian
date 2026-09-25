using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Persists <see cref="ProviderInteractionClaim"/> — see
/// <c>.ai_docs/human-acq-1-matrix-authorize-plan.md</c> §2 for why claim state
/// lives in its own table rather than on <see cref="ProviderAcquisitionJob"/>.
/// </summary>
public interface IProviderInteractionClaimStore
{
    Task<ProviderInteractionClaim?> FindAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to insert a new claim. Returns <see langword="false"/> — without
    /// throwing — if another claim already exists for this job, which is this
    /// store's atomicity mechanism: two concurrent claimants race on the
    /// database's own unique-primary-key check, not on application logic.
    /// </summary>
    Task<bool> TryInsertAsync(ProviderInteractionClaim claim, CancellationToken cancellationToken);

    /// <summary>Releases only the provisional claim made by this alert recipient, if it still owns the row.</summary>
    Task ReleaseIfOwnedAsync(Guid jobId, Guid userId, Guid alertId, CancellationToken cancellationToken);

    void Remove(ProviderInteractionClaim claim);

    Task<IReadOnlyList<ProviderInteractionClaim>> ListAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
