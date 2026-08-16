using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Policy;
using FamilyLibrarian.Domain.Policy;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Policy;

public sealed class AcquisitionPolicySettingsStore(AppDbContext database, IClock clock) : IAcquisitionPolicySettingsStore
{
    public Task<AcquisitionPolicySettings?> FindAsync(CancellationToken cancellationToken) =>
        database.AcquisitionPolicySettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<AcquisitionPolicySettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.AcquisitionPolicySettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new AcquisitionPolicySettings(clock.UtcNow);
        database.AcquisitionPolicySettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
