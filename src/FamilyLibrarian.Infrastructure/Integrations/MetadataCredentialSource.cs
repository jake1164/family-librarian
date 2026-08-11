using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Infrastructure.Metadata;
using Microsoft.Extensions.Configuration;

namespace FamilyLibrarian.Infrastructure.Integrations;

/// <summary>
/// Resolves the plaintext credential for a provider at the moment it is needed.
/// </summary>
/// <remarks>
/// Deployment configuration wins over the stored value: an operator using an
/// external secret manager should not have their key shadowed by a stale row.
/// <para>
/// This reads the database on each outbound provider request rather than caching.
/// A search issues a small number of provider calls, and one primary-key lookup
/// is negligible beside the external API round trip — while an uncached read
/// means a replaced key takes effect immediately, with no invalidation to get
/// wrong. Revisit only if provider call volume grows.
/// </para>
/// </remarks>
public sealed class MetadataCredentialSource(
    IConfiguration configuration,
    IMetadataProviderSettingsStore store,
    ICredentialProtector protector)
{
    public async Task<string?> GetCredentialAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        var configured = ReadFromConfiguration(providerId);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var setting = await store.FindAsync(providerId, cancellationToken);
        if (setting?.ProtectedCredential is null)
        {
            return null;
        }

        return protector.Unprotect(
            providerId,
            setting.ProtectedCredential,
            setting.CredentialFormatVersion);
    }

    private string? ReadFromConfiguration(string providerId) =>
        string.Equals(providerId, MetadataProviderRegistry.GoogleBooksProviderId, StringComparison.OrdinalIgnoreCase)
            ? configuration[$"{GoogleBooksMetadataOptions.SectionName}:ApiKey"]
            : null;
}
