using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class OutboundCommunicationRepository(AppDbContext database) : IOutboundCommunicationStore
{
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

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
