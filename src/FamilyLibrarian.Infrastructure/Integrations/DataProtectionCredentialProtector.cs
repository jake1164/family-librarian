using System.Collections.Concurrent;
using FamilyLibrarian.Application.Integrations;
using Microsoft.AspNetCore.DataProtection;

namespace FamilyLibrarian.Infrastructure.Integrations;

/// <summary>
/// Protects provider credentials with the host's Data Protection key ring.
/// </summary>
/// <remarks>
/// The purpose string is part of the derived key, so a value protected here
/// cannot be unprotected by any other purpose in the application — an auth
/// cookie payload and a provider API key are not interchangeable ciphertexts.
/// The provider id is appended as a sub-purpose, which extends that separation
/// between providers as well.
/// </remarks>
public sealed class DataProtectionCredentialProtector : ICredentialProtector
{
    internal const string PurposeRoot = "FamilyLibrarian.MetadataProviderCredential.v1";

    private const int CurrentFormatVersion = 1;

    private readonly IDataProtectionProvider _provider;
    private readonly ConcurrentDictionary<string, IDataProtector> _protectors =
        new(StringComparer.OrdinalIgnoreCase);

    public DataProtectionCredentialProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public int FormatVersion => CurrentFormatVersion;

    public string Protect(string providerId, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        return ProtectorFor(providerId).Protect(plaintext);
    }

    public string? Unprotect(string providerId, string protectedValue, int formatVersion)
    {
        if (string.IsNullOrWhiteSpace(providerId) ||
            string.IsNullOrEmpty(protectedValue) ||
            formatVersion != CurrentFormatVersion)
        {
            return null;
        }

        try
        {
            return ProtectorFor(providerId).Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key ring was lost, rotated past its revocation window, the
            // stored value predates the current format, or it belongs to another
            // provider. Returning null lets the provider report "not configured"
            // instead of crashing every search; the admin re-enters the key.
            return null;
        }
    }

    private IDataProtector ProtectorFor(string providerId) =>
        _protectors.GetOrAdd(
            providerId,
            static (id, provider) => provider.CreateProtector(PurposeRoot, id.ToLowerInvariant()),
            _provider);
}
