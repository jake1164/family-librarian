namespace FamilyLibrarian.Application.Abstractions;

/// <summary>
/// Creates unguessable, URL-safe bearer tokens and hashes them for storage — the
/// same shape as <see cref="Accounts.IInvitationTokenGenerator"/> (deliberately:
/// <c>InvitationTokenGenerator</c> implements both, so the crypto lives in
/// exactly one place). Used by HUMAN-ACQ-1's Matrix magic-link tokens, a second
/// bearer-secret use case with the same never-store-plaintext requirement as an
/// invitation token.
/// </summary>
public interface ISecureTokenGenerator
{
    /// <summary>A fresh, unguessable, URL-safe token. Returned to the caller once.</summary>
    string CreateToken();

    /// <summary>The stored form of a token.</summary>
    string Hash(string token);
}
