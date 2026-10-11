using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.ModelGateway;

public sealed class ModelGatewaySettingsStore(AppDbContext database, IClock clock) : IModelGatewaySettingsStore
{
    public Task<ModelGatewaySettings?> FindAsync(CancellationToken cancellationToken) =>
        database.ModelGatewaySettingsRows.AsNoTracking().Include(settings => settings.Roles).FirstOrDefaultAsync(cancellationToken);

    public async Task<ModelGatewaySettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.ModelGatewaySettingsRows.Include(settings => settings.Roles).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new ModelGatewaySettings(clock.UtcNow);
        database.ModelGatewaySettingsRows.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
