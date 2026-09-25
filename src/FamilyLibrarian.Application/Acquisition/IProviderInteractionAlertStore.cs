using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Persists <see cref="ProviderInteractionAlert"/> and its recipients.</summary>
public interface IProviderInteractionAlertStore
{
    /// <summary>The provider's currently open (Open or Claimed) alert, if any — HUMAN-ACQ-1 D5.</summary>
    Task<ProviderInteractionAlert?> FindOpenForProviderAsync(Guid externalProviderId, CancellationToken cancellationToken);

    /// <summary>Every open (Open or Claimed) alert, with recipients loaded — the alert worker's per-pass working set.</summary>
    Task<IReadOnlyList<ProviderInteractionAlert>> ListOpenAsync(CancellationToken cancellationToken);

    /// <summary>Looks up a recipient by its token hash, with the owning alert loaded. Never by plaintext token.</summary>
    Task<ProviderInteractionAlertRecipient?> FindRecipientByTokenHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Loads one alert (with recipients) by id — used to reload after a concurrency conflict with the worker.</summary>
    Task<ProviderInteractionAlert?> FindByIdAsync(Guid alertId, CancellationToken cancellationToken);

    /// <summary>
    /// The most recently closed alert for this provider, if any — used to decide
    /// whether a fresh alert should skip the usual send delay (a re-alert after a
    /// lease lapse should go out promptly, not wait <c>SendDelaySeconds</c> again).
    /// </summary>
    Task<ProviderInteractionAlert?> FindMostRecentlyClosedForProviderAsync(
        Guid externalProviderId, CancellationToken cancellationToken);

    void Add(ProviderInteractionAlert alert);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves pending changes; returns <see langword="false"/> instead of throwing
    /// if a concurrent writer (typically the alert worker's own pass) already
    /// changed one of the entities being saved. On <see langword="false"/>, the
    /// conflicting entities are detached, so a subsequent <see cref="FindByIdAsync"/>
    /// or <see cref="FindRecipientByTokenHashAsync"/> call performs a genuine
    /// fresh read rather than returning the same stale tracked instances.
    /// </summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);
}
