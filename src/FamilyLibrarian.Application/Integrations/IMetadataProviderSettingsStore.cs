using FamilyLibrarian.Domain.Integrations;

namespace FamilyLibrarian.Application.Integrations;

public interface IMetadataProviderSettingsStore
{
    Task<IReadOnlyList<MetadataProviderSetting>> GetAllAsync(CancellationToken cancellationToken);

    Task<MetadataProviderSetting?> FindAsync(string providerId, CancellationToken cancellationToken);

    /// <summary>Returns the existing row, or a new unsaved one when the provider has never been configured.</summary>
    Task<MetadataProviderSetting> GetOrCreateAsync(string providerId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
