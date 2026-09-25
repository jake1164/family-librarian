using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Acquisition;

public sealed class ProviderInteractionAlertStore(AppDbContext database) : IProviderInteractionAlertStore
{
    public Task<ProviderInteractionAlert?> FindOpenForProviderAsync(
        Guid externalProviderId, CancellationToken cancellationToken) =>
        database.ProviderInteractionAlerts
            .Include(alert => alert.Recipients)
            .FirstOrDefaultAsync(
                alert => alert.ExternalProviderId == externalProviderId &&
                    (alert.State == ProviderInteractionAlertState.Open ||
                        alert.State == ProviderInteractionAlertState.Claimed),
                cancellationToken);

    public async Task<IReadOnlyList<ProviderInteractionAlert>> ListOpenAsync(CancellationToken cancellationToken) =>
        await database.ProviderInteractionAlerts
            .Include(alert => alert.Recipients)
            .Where(alert => alert.State == ProviderInteractionAlertState.Open ||
                alert.State == ProviderInteractionAlertState.Claimed)
            .ToArrayAsync(cancellationToken);

    public Task<ProviderInteractionAlertRecipient?> FindRecipientByTokenHashAsync(
        string tokenHash, CancellationToken cancellationToken) =>
        database.ProviderInteractionAlertRecipients
            .Include(recipient => recipient.Alert)
            .ThenInclude(alert => alert.Recipients)
            .FirstOrDefaultAsync(recipient => recipient.TokenHash == tokenHash, cancellationToken);

    public Task<ProviderInteractionAlert?> FindByIdAsync(Guid alertId, CancellationToken cancellationToken) =>
        database.ProviderInteractionAlerts
            .Include(alert => alert.Recipients)
            .FirstOrDefaultAsync(alert => alert.Id == alertId, cancellationToken);

    public Task<ProviderInteractionAlert?> FindMostRecentlyClosedForProviderAsync(
        Guid externalProviderId, CancellationToken cancellationToken) =>
        database.ProviderInteractionAlerts
            .Where(alert => alert.ExternalProviderId == externalProviderId && alert.ClosedAtUtc != null)
            .OrderByDescending(alert => alert.ClosedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(ProviderInteractionAlert alert) => database.ProviderInteractionAlerts.Add(alert);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // The alert and its recipients form one aggregate. Detach the whole
            // graph so a retry cannot reuse a stale, tracked recipient after the
            // losing SaveChanges transaction was rolled back.
            foreach (var entry in database.ChangeTracker.Entries()
                .Where(entry => entry.Entity is ProviderInteractionAlert or ProviderInteractionAlertRecipient)
                .ToArray())
                entry.State = EntityState.Detached;

            return false;
        }
    }
}
