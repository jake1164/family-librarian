using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

public interface IProviderSettingsStore
{
    Task<IReadOnlyList<ProviderSetting>> GetAllAsync(CancellationToken cancellationToken);

    Task<ProviderSetting?> FindAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>Returns the existing row, or a new unsaved one when the provider has never been configured.</summary>
    Task<ProviderSetting> GetOrCreateAsync(string providerId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
