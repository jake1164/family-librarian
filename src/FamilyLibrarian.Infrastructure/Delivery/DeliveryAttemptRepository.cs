using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Domain.Delivery;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Publishing;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
                attempt.BookTitle ?? (work == null ? null : work.CanonicalTitle),
                user.DisplayName,
                user.Email!,
                attempt.ExternalBookId,
                attempt.Status,
                attempt.AttemptNumber,
                attempt.FailureReason,
                attempt.CreatedAtUtc,
                attempt.CompletedAtUtc,
                attempt.DeliveryId,
                attempt.ConfirmationStatus,
                attempt.ConfirmedAtUtc,
                !database.DeliveryAttempts.Any(later => later.DeliveryId == attempt.DeliveryId &&
                    later.AttemptNumber > attempt.AttemptNumber),
                attempt.IsRetryable);

        return await query.ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryAttempt>> ListRetryableFailedAsync(
        DateTimeOffset olderThanUtc, int maxAttemptNumber, CancellationToken cancellationToken) =>
        await database.DeliveryAttempts
            .Where(attempt =>
                !database.DeliveryAttempts.Any(later => later.DeliveryId == attempt.DeliveryId &&
                    later.AttemptNumber > attempt.AttemptNumber) &&
                attempt.Status == DeliveryAttemptStatus.Failed &&
                attempt.IsRetryable &&
                attempt.AttemptNumber < maxAttemptNumber &&
                attempt.CompletedAtUtc != null &&
                attempt.CompletedAtUtc <= olderThanUtc)
            .ToArrayAsync(cancellationToken);

    public Task<DeliveryAttempt?> FindLatestAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        database.DeliveryAttempts.Where(attempt => attempt.DeliveryId == deliveryId)
            .OrderByDescending(attempt => attempt.AttemptNumber).FirstOrDefaultAsync(cancellationToken);

    public Task<DeliveryTarget?> GetEligibleTargetAsync(DeliveryAttempt attempt, CancellationToken cancellationToken) =>
        database.DeliveryTargets.AsNoTracking().Where(target =>
            target.Id == attempt.DeliveryTargetId && target.UserId == attempt.UserId && target.IsEnabled &&
            database.Users.Any(user => user.Id == attempt.UserId && user.Status == UserStatus.Active) &&
            (attempt.RequestId == null || database.BookRequests.Any(request => request.Id == attempt.RequestId &&
                request.Status != RequestStatus.Cancelled && request.Participants.Any(participant =>
                    participant.UserId == attempt.UserId && participant.WithdrawnAtUtc == null &&
                    participant.WantsEbook && participant.DeliveryTargetId == target.Id))))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryAddAsync(DeliveryAttempt attempt, CancellationToken cancellationToken)
    {
        database.DeliveryAttempts.Add(attempt);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_delivery_chain_attempt" or "ux_delivery_request_user" })
        {
            database.Entry(attempt).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> TryTransitionAsync(DeliveryAttempt attempt, DeliveryAttemptStatus status,
        DateTimeOffset atUtc, CancellationToken cancellationToken, string? reason = null, bool retryable = false)
    {
        attempt.TransitionTo(status, atUtc, reason, retryable);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException exception) when (exception.Entries.All(entry => entry.Entity == attempt))
        {
            await database.Entry(attempt).ReloadAsync(cancellationToken);
            return false;
        }
    }

    public async Task<IReadOnlyList<DeliveryAttempt>> ListUnfinishedAsync(CancellationToken cancellationToken) =>
        await database.DeliveryAttempts.Where(attempt =>
            attempt.Status == DeliveryAttemptStatus.Pending || attempt.Status == DeliveryAttemptStatus.Submitting)
            .OrderBy(attempt => attempt.CreatedAtUtc).ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<ReadyRequestDelivery>> ListUnreleasedAsync(CancellationToken cancellationToken)
    {
        var ready = await (from import in database.LibraryImports
            join asset in database.MediaAssets on import.AssetId equals asset.Id
            join format in database.RequestFormats on asset.AssociatedRequestFormatId equals format.Id
            join request in database.BookRequests on format.RequestId equals request.Id
            join work in database.Works on request.WorkId equals work.Id
            where import.Status == LibraryImportStatus.Available && import.ExternalBookId != null &&
                format.MediaType == RequestMediaType.Ebook && format.Status == RequestFormatStatus.Available &&
                request.Status != RequestStatus.Cancelled && request.Participants.Any(participant =>
                    participant.WithdrawnAtUtc == null && participant.WantsEbook && participant.DeliveryTargetId != null &&
                    !database.DeliveryAttempts.Any(attempt => attempt.RequestId == request.Id && attempt.UserId == participant.UserId) &&
                    database.DeliveryTargets.Any(target => target.Id == participant.DeliveryTargetId && target.IsEnabled &&
                        target.UserId == participant.UserId) &&
                    database.Users.Any(user => user.Id == participant.UserId && user.Status == UserStatus.Active))
            orderby import.CompletedAtUtc descending
            select new { RequestId = request.Id, import.ExternalBookId, asset.Format, work.CanonicalTitle })
            .ToArrayAsync(cancellationToken);
        var result = new List<ReadyRequestDelivery>();
        foreach (var item in ready.DistinctBy(item => item.RequestId))
        {
            var request = await database.BookRequests.Include(request => request.Participants)
                .Include(request => request.Formats).SingleAsync(request => request.Id == item.RequestId, cancellationToken);
            result.Add(new ReadyRequestDelivery(request, item.ExternalBookId!, item.Format, item.CanonicalTitle));
        }
        return result;
    }

    public void Add(DeliveryAttempt attempt) => database.DeliveryAttempts.Add(attempt);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
