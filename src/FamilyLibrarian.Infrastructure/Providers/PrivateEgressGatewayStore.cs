using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Providers;

public sealed class PrivateEgressGatewayStore(AppDbContext database, IClock clock) : IPrivateEgressGatewayStore
{
    public Task<PrivateEgressGatewaySettings?> FindAsync(CancellationToken cancellationToken) =>
        database.PrivateEgressGatewaySettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<PrivateEgressGatewaySettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.PrivateEgressGatewaySettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new PrivateEgressGatewaySettings(clock.UtcNow);
        database.PrivateEgressGatewaySettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
