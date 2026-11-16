using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Application.Integrations;

/// <summary>
/// Supplies the providers that should actually be queried right now.
/// </summary>
/// <remarks>
/// Search and candidate-detail endpoints resolve providers through this instead
/// of injecting <c>IEnumerable&lt;IBookMetadataProvider&gt;</c> directly. All
/// installed providers are registered in DI; enablement is a runtime decision
/// read from the database on each request, so an administrator toggling a
/// provider takes effect without restarting the host.
/// </remarks>
public interface IActiveMetadataProviderResolver
{
    Task<IReadOnlyList<IBookMetadataProvider>> GetActiveProvidersAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the named provider only when it is currently usable, so a disabled
    /// provider cannot be reached by addressing it directly.
    /// </summary>
    Task<IBookMetadataProvider?> FindActiveProviderAsync(
        string providerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether an implementation with this id is installed at all, enabled or not. Only for telling "no such source" from
    /// "that source is switched off" when reporting an error; it never authorizes calling the provider.
    /// </summary>
    bool IsRegistered(string providerId);

    /// <summary>
    /// The display name of an installed provider, enabled or not, or null when no implementation with this id is installed (a provider
    /// whose configuration was removed). Used only to label stored evidence; the default reports none.
    /// </summary>
    string? RegisteredDisplayName(string providerId) => null;
}
