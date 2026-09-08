using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Delivery;

public sealed class DeliveryAttemptRepository(AppDbContext database) : IDeliveryAttemptRepository
{
    public Task<DeliveryAttempt?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.DeliveryAttempts.FirstOrDefaultAsync(attempt => attempt.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DeliveryAttempt>> ListForRequestAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        await database.DeliveryAttempts
            .Where(attempt => attempt.RequestId == requestId)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<DeliveryAttempt>> ListForUserAsync(
        Guid userId, CancellationToken cancellationToken) =>
        await database.DeliveryAttempts
            .Where(attempt => attempt.UserId == userId)
            .OrderByDescending(attempt => attempt.CreatedAtUtc)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<DeliveryAttempt>> ListRetryableFailedAsync(
        DateTimeOffset olderThanUtc, int maxAttemptNumber, CancellationToken cancellationToken) =>
        await database.DeliveryAttempts
            .Where(attempt =>
                attempt.Status == DeliveryAttemptStatus.Failed &&
                attempt.IsRetryable &&
                attempt.AttemptNumber < maxAttemptNumber &&
                attempt.CompletedAtUtc != null &&
                attempt.CompletedAtUtc <= olderThanUtc)
            .ToArrayAsync(cancellationToken);

    public void Add(DeliveryAttempt attempt) => database.DeliveryAttempts.Add(attempt);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
