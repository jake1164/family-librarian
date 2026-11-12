using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class OutboundCommunicationRepository(AppDbContext database) : IOutboundCommunicationStore, IAcquisitionQuestionCommunicationLookup
{
    public Task<OutboundCommunication?> FindQuestionAsync(Guid userId, string? eventId, CancellationToken cancellationToken)
    {
        var query = database.OutboundCommunications.AsNoTracking().Where(c => c.RecipientUserId == userId &&
            c.CommunicationType == OutboundCommunicationTypes.AcquisitionQuestionAsked && c.RelatedEntityType == "BookRequest");
        query = eventId is not null ? query.Where(c => c.MatrixEventId == eventId) : query.Where(c =>
            database.BookRequests.Any(r => r.Id == c.RelatedEntityId && r.QuestionKind != null && c.CreatedAtUtc >= r.QuestionAskedAtUtc));
        return query.OrderByDescending(c => c.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
    }

    public Task EnqueueAsync(OutboundCommunication communication, CancellationToken cancellationToken)
    {
        database.OutboundCommunications.Add(communication);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<OutboundCommunication>> GetUnprocessedBatchAsync(
        int maxCount, CancellationToken cancellationToken) =>
        await database.OutboundCommunications
            .Include(communication => communication.Deliveries)
            .Where(communication => communication.ProcessedAtUtc == null)
            .OrderBy(communication => communication.CreatedAtUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

    public Task<OutboundCommunication?> FindMostRecentByTypeAsync(
        Guid recipientUserId, string communicationType, CancellationToken cancellationToken) =>
        database.OutboundCommunications
            .Where(communication =>
                communication.RecipientUserId == recipientUserId && communication.CommunicationType == communicationType)
            .OrderByDescending(communication => communication.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
