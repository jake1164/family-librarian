using FamilyLibrarian.Domain.Providers;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The full administrator-visible state of one provider.
/// </summary>
/// <remarks>
/// There is deliberately no credential property here. The only credential facts
/// that leave the host are <see cref="HasStoredCredential"/>,
/// <see cref="CredentialHint"/> (last four characters), and
/// <see cref="CredentialSetAtUtc"/>.
/// </remarks>
public sealed record ProviderStatus(
    string ProviderId,
    string DisplayName,
    IReadOnlySet<ProviderCapability> Capabilities,
    bool RequiresCredential,
    bool IsEnabled,
    bool HasStoredCredential,
    bool IsExternallyManaged,
    string? CredentialHint,
    DateTimeOffset? CredentialSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    string? SetupInstructions,
    IReadOnlyList<ProviderSetupLink> SetupLinks)
{
    /// <summary>
    /// True when the provider is switched on but still lacks the credential it
    /// needs, so search will silently skip it.
    /// </summary>
    public bool IsMisconfigured =>
        IsEnabled && RequiresCredential && !HasStoredCredential && !IsExternallyManaged;

    /// <summary>True when an admin may write or clear the stored credential.</summary>
    public bool CanManageCredential => RequiresCredential && !IsExternallyManaged;
}
