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

    public async Task<IReadOnlyList<DeliveryAttemptView>> ListRecentAsync(CancellationToken cancellationToken)
    {
        // Left join to BookRequest -- the existing-book fast path has none --
        // and requester identity is always available via UserId directly,
        // mirroring RequestRepository.ProjectAdminViews' admin-only user join.
        var query =
            from attempt in database.DeliveryAttempts
            join user in database.Users on attempt.UserId equals user.Id
            join request in database.BookRequests on attempt.RequestId equals request.Id into requestJoin
            from request in requestJoin.DefaultIfEmpty()
            join work in database.Works on request.WorkId equals work.Id into workJoin
            from work in workJoin.DefaultIfEmpty()
            orderby attempt.CreatedAtUtc descending
            select new DeliveryAttemptView(
                attempt.Id,
                attempt.RequestId,
                request == null ? (Guid?)null : request.WorkId,
                work == null ? null : work.CanonicalTitle,
                user.DisplayName,
                user.Email!,
                attempt.ExternalBookId,
                attempt.Status,
                attempt.AttemptNumber,
                attempt.FailureReason,
                attempt.CreatedAtUtc,
                attempt.CompletedAtUtc);

        return await query.ToArrayAsync(cancellationToken);
    }

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
