using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using FamilyLibrarian.Application.Accounts;

namespace FamilyLibrarian.Infrastructure.Identity;

/// <summary>
/// Creates 256-bit invitation tokens and hashes them for storage.
/// </summary>
/// <remarks>
/// A single unsalted SHA-256 is the right choice here, unlike for a password.
/// The token is 256 bits of cryptographic randomness, so there is no dictionary
/// to attack and no work factor worth paying; the hash exists so that reading the
/// table yields nothing usable. A per-row salt would also make lookup-by-hash
/// impossible without scanning every row.
/// </remarks>
public sealed class InvitationTokenGenerator : IInvitationTokenGenerator
{
    private const int TokenBytes = 32;

    public string CreateToken() =>
        // URL-safe and unpadded: the token travels in a link the administrator
        // copies and pastes, so it must survive that without escaping.
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(digest);
    }
}
