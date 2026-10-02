using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The single rule for whether a provider is effectively on, and whether it has
/// the credential it needs.
/// </summary>
/// <remarks>
/// Both the search path and the admin surface resolve provider state through
/// here. Keeping one implementation is what stops the Admin page from reporting
/// "enabled" while search quietly skips the provider.
/// </remarks>
public static class ProviderState
{
    /// <summary>
    /// A stored setting wins once it exists; otherwise the deployment default
    /// applies. An externally managed provider follows deployment configuration
    /// entirely.
    /// </summary>
    public static bool IsEnabled(
        ProviderDescriptor descriptor,
        ProviderSetting? setting)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.HasExternallyManagedCredential)
        {
            return descriptor.DefaultEnabled;
        }

        return setting?.IsEnabled ?? descriptor.DefaultEnabled;
    }

    public static bool HasCredential(
        ProviderDescriptor descriptor,
        ProviderSetting? setting)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!descriptor.RequiresCredential)
        {
            return true;
        }

        return descriptor.HasExternallyManagedCredential || (setting?.HasStoredCredential ?? false);
    }

    /// <summary>True when the provider should actually be queried during a search.</summary>
    public static bool IsUsable(
        ProviderDescriptor descriptor,
        ProviderSetting? setting) =>
        IsEnabled(descriptor, setting) && HasCredential(descriptor, setting);

    /// <summary>
    /// Resolves a provider by id and applies <see cref="IsUsable"/> to its stored
    /// setting. Every use of a provider -- lookup, download, catalogue sync --
    /// goes through this so a disabled provider is never contacted or updated.
    /// An id the registry does not know is not usable.
    /// </summary>
    public static async Task<bool> IsUsableAsync(
        IProviderRegistry registry,
        IProviderSettingsStore settingsStore,
        string providerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(settingsStore);

        var descriptor = registry.Find(providerId);
        return descriptor is not null &&
               IsUsable(descriptor, await settingsStore.FindAsync(providerId, cancellationToken));
    }
}
