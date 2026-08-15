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
                    $"{GoogleBooksMetadataOptions.SectionName}:Enabled", false),
                SetupInstructions:
                    "Google Books API keys are free. In Google Cloud Console: " +
                    "1) create or choose a project, 2) enable the Books API for it, " +
                    "3) create an API key under Credentials and restrict it to the Books API. " +
                    "Google enforces its own daily query quota on the key (adjustable in the " +
                    "console); Family Librarian does not add its own rate limit on top of it, " +
                    "unlike Open Library, which this app throttles itself.",
                SetupLinks:
                [
                    new MetadataProviderSetupLink(
                        "1. Enable the Books API",
                        "https://console.cloud.google.com/apis/library/books.googleapis.com"),
                    new MetadataProviderSetupLink(
                        "2. Create an API key",
                        "https://console.cloud.google.com/apis/credentials")
                ])
        ];
    }

    public IReadOnlyList<MetadataProviderDescriptor> GetInstalledProviders() => _providers;

    public MetadataProviderDescriptor? Find(string providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
}
