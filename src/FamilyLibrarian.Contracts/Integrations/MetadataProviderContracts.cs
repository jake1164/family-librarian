namespace FamilyLibrarian.Contracts.Integrations;

/// <summary>
/// Administrator-visible provider state.
/// </summary>
/// <remarks>
/// There is intentionally no credential field. The client learns only that a
/// credential exists, its last four characters, and when it was set — never the
/// value. Adding a credential property here would defeat the write-only design,
/// so it must stay absent.
/// </remarks>
public sealed record MetadataProviderStatusResponse(
    string ProviderId,
    string DisplayName,
    bool RequiresCredential,
    bool IsEnabled,
    bool HasStoredCredential,
    bool IsExternallyManaged,
    bool IsMisconfigured,
    bool CanManageCredential,
    string? CredentialHint,
    DateTimeOffset? CredentialSetAtUtc,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    string? SetupInstructions,
    IReadOnlyList<MetadataProviderSetupLinkResponse> SetupLinks);

public sealed record MetadataProviderSetupLinkResponse(string Label, string Url);

public sealed record MetadataProviderListResponse(
    IReadOnlyList<MetadataProviderStatusResponse> Providers);

public sealed record SetMetadataProviderEnabledRequest(bool Enabled);

public sealed record SetMetadataProviderCredentialRequest(string Credential);

public sealed record MetadataProviderTestResponse(
    bool Succeeded,
    string Message,
    MetadataProviderStatusResponse Provider);
