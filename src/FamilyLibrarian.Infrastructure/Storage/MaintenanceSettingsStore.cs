using FamilyLibrarian.Application.Storage;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Storage;

public sealed class MaintenanceSettingsStore(AppDbContext database) : IMaintenanceSettingsStore
{
    public Task<MaintenanceSettings?> FindAsync(CancellationToken cancellationToken) => database.MaintenanceSettings.FirstOrDefaultAsync(cancellationToken);

    public void Add(MaintenanceSettings settings) => database.MaintenanceSettings.Add(settings);

    public void Remove(MaintenanceSettings settings) => database.MaintenanceSettings.Remove(settings);

    public Task SaveAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
