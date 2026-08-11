using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using Microsoft.Extensions.Configuration;

namespace FamilyLibrarian.Infrastructure.Integrations;

/// <summary>
/// The fixed set of providers compiled into this build.
/// </summary>
/// <remarks>
/// The list is a hard-coded allowlist on purpose. Admin routes resolve a caller's
/// provider id against it, so no route can address an arbitrary id, and there is
/// no path by which configuration or a request body introduces a new provider,
/// executable code, or an unrestricted target URL.
/// </remarks>
public sealed class MetadataProviderRegistry : IMetadataProviderRegistry
{
    public const string DemoProviderId = "demo";
    public const string OpenLibraryProviderId = "openlibrary";
    public const string GoogleBooksProviderId = "googlebooks";

    private readonly MetadataProviderDescriptor[] _providers;

    public MetadataProviderRegistry(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // A deployment-supplied key is a read-only override: the operator manages
        // it outside the app, so the admin UI must not offer to replace it.
        var googleBooksKeyFromConfiguration = !string.IsNullOrWhiteSpace(
            configuration[$"{GoogleBooksMetadataOptions.SectionName}:ApiKey"]);

        _providers =
        [
            new MetadataProviderDescriptor(
                DemoProviderId,
                "Family Librarian sample catalog",
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                DefaultEnabled: configuration.GetValue("MetadataProviders:Demo:Enabled", true)),
            new MetadataProviderDescriptor(
                OpenLibraryProviderId,
                "Open Library",
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                DefaultEnabled: configuration.GetValue(
                    $"{OpenLibraryMetadataOptions.SectionName}:Enabled", false)),
            new MetadataProviderDescriptor(
                GoogleBooksProviderId,
                "Google Books",
                RequiresCredential: true,
                HasExternallyManagedCredential: googleBooksKeyFromConfiguration,
                DefaultEnabled: configuration.GetValue(
                    $"{GoogleBooksMetadataOptions.SectionName}:Enabled", false))
        ];
    }

    public IReadOnlyList<MetadataProviderDescriptor> GetInstalledProviders() => _providers;

    public MetadataProviderDescriptor? Find(string providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
}
