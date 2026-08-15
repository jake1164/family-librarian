using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Infrastructure.Integrations;

public sealed class ActiveMetadataProviderResolver(
    IEnumerable<IBookMetadataProvider> providers,
    IProviderRegistry registry,
    IProviderSettingsStore store) : IActiveMetadataProviderResolver
{
    public async Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);

        return providers
            .Where(provider => IsUsable(provider.Id, settings))
            .ToArray();
    }

    public async Task<IBookMetadataProvider?> FindActiveProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var provider = providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return null;
        }

        var settings = await LoadSettingsAsync(cancellationToken);
        return IsUsable(provider.Id, settings) ? provider : null;
    }

    private async Task<Dictionary<string, ProviderSetting>> LoadSettingsAsync(
        CancellationToken cancellationToken)
    {
        var settings = await store.GetAllAsync(cancellationToken);
        return settings.ToDictionary(
            setting => setting.ProviderId,
            StringComparer.OrdinalIgnoreCase);
    }

    private bool IsUsable(
        string providerId,
        Dictionary<string, ProviderSetting> settings)
    {
        var descriptor = registry.Find(providerId);
        if (descriptor is null)
        {
            // A provider implementation with no registry entry is a wiring
            // mistake. Treat it as unusable rather than querying something the
            // admin surface cannot see or switch off.
            return false;
        }

        settings.TryGetValue(descriptor.Id, out var setting);
        return ProviderState.IsUsable(descriptor, setting);
    }
}
