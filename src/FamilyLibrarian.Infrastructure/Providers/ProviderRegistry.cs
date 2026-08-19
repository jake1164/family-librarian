using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Infrastructure.Metadata;
using Microsoft.Extensions.Configuration;

namespace FamilyLibrarian.Infrastructure.Providers;

/// <summary>
/// The fixed set of providers compiled into this build.
/// </summary>
/// <remarks>
/// The list is a hard-coded allowlist on purpose. Admin routes resolve a caller's
/// provider id against it, so no route can address an arbitrary id, and there is
/// no path by which configuration or a request body introduces a new provider,
/// executable code, or an unrestricted target URL.
/// </remarks>
public sealed class ProviderRegistry : IProviderRegistry
{
    public const string DemoProviderId = "demo";
    public const string OpenLibraryProviderId = "openlibrary";
    public const string GoogleBooksProviderId = "googlebooks";
    public const string GutendexProviderId = "gutendex";

    private static readonly IReadOnlySet<ProviderCapability> MetadataOnly =
        new HashSet<ProviderCapability> { ProviderCapability.Metadata };

    private static readonly IReadOnlySet<ProviderCapability> DirectAcquisitionOnly =
        new HashSet<ProviderCapability> { ProviderCapability.DirectAcquisition };

    private readonly ProviderDescriptor[] _providers;

    public ProviderRegistry(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // A deployment-supplied key is a read-only override: the operator manages
        // it outside the app, so the admin UI must not offer to replace it.
        var googleBooksKeyFromConfiguration = !string.IsNullOrWhiteSpace(
            configuration[$"{GoogleBooksMetadataOptions.SectionName}:ApiKey"]);

        _providers =
        [
            new ProviderDescriptor(
                DemoProviderId,
                "Family Librarian sample catalog",
                MetadataOnly,
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                DefaultEnabled: configuration.GetValue("MetadataProviders:Demo:Enabled", true)),
            new ProviderDescriptor(
                OpenLibraryProviderId,
                "Open Library",
                MetadataOnly,
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                DefaultEnabled: configuration.GetValue(
                    $"{OpenLibraryMetadataOptions.SectionName}:Enabled", false),
                SetupInstructions:
                    "Open Library is an open catalogue from the Internet Archive. " +
                    "Family Librarian uses its public API for book discovery.",
                SetupLinks:
                [
                    new ProviderSetupLink("Explore Open Library", "https://openlibrary.org/"),
                    new ProviderSetupLink("API documentation", "https://openlibrary.org/developers/api")
                ]),
            new ProviderDescriptor(
                GoogleBooksProviderId,
                "Google Books",
                MetadataOnly,
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
                    new ProviderSetupLink("Explore Google Books", "https://books.google.com/"),
                    new ProviderSetupLink(
                        "Books API documentation",
                        "https://developers.google.com/books/docs/overview"),
                    new ProviderSetupLink(
                        "Enable the Books API",
                        "https://console.cloud.google.com/apis/library/books.googleapis.com"),
                    new ProviderSetupLink(
                        "Create an API key",
                        "https://console.cloud.google.com/apis/credentials")
                ]),
            new ProviderDescriptor(
                GutendexProviderId,
                "Gutendex (Project Gutenberg)",
                DirectAcquisitionOnly,
                RequiresCredential: false,
                HasExternallyManagedCredential: false,
                // This narrowly scoped, credential-free public-domain source is
                // ready to use without an admin setup step. It is only queried
                // after an ebook request, and its files still pass the full
                // quarantine, malware, format, and identity pipeline.
                DefaultEnabled: true,
                SetupInstructions:
                    "Gutendex is a JSON API that indexes Project Gutenberg's " +
                    "public-domain ebook catalogue.",
                SetupLinks:
                [
                    new ProviderSetupLink("Gutendex API", "https://gutendex.com/"),
                    new ProviderSetupLink("About Project Gutenberg", "https://www.gutenberg.org/about/")
                ])
        ];
    }

    public IReadOnlyList<ProviderDescriptor> GetInstalledProviders() => _providers;

    public ProviderDescriptor? Find(string providerId) =>
        string.IsNullOrWhiteSpace(providerId)
            ? null
            : _providers.FirstOrDefault(provider =>
                string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
}
