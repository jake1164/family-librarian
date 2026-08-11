namespace FamilyLibrarian.Application.Integrations;

/// <summary>
/// Encrypts and decrypts provider credentials using the host's key ring.
/// </summary>
/// <remarks>
/// Implemented in Infrastructure over ASP.NET Core Data Protection, whose key
/// ring lives in PostgreSQL (see <c>AddDataProtectionKeys</c>) so a container
/// restart does not orphan every stored credential.
/// <para>
/// Every method takes the provider id because protection purposes are separated
/// per provider: ciphertext written for one provider cannot be unprotected as
/// another's, so a mis-set row or a swapped column cannot leak one integration's
/// key into another's outbound requests.
/// </para>
/// </remarks>
public interface ICredentialProtector
{
    /// <summary>The format version stamped onto values this protector produces.</summary>
    int FormatVersion { get; }

    string Protect(string providerId, string plaintext);

    /// <summary>
    /// Returns the plaintext, or <c>null</c> when the value cannot be unprotected
    /// — a lost or rotated key ring, a value written in an older format, or
    /// ciphertext belonging to a different provider.
    /// </summary>
    string? Unprotect(string providerId, string protectedValue, int formatVersion);
}
