namespace FamilyLibrarian.Contracts.Providers;

/// <summary>
/// Administrator-visible provider state.
/// </summary>
/// <remarks>
/// There is intentionally no credential field. The client learns only that a
/// credential exists, its last four characters, and when it was set — never the
/// value. Adding a credential property here would defeat the write-only design,
/// so it must stay absent.
/// </remarks>
public sealed record ProviderStatusResponse(
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
    IReadOnlyList<ProviderSetupLinkResponse> SetupLinks);

public sealed record ProviderSetupLinkResponse(string Label, string Url);

public sealed record ProviderListResponse(
    IReadOnlyList<ProviderStatusResponse> Providers);

public sealed record SetProviderEnabledRequest(bool Enabled);

public sealed record SetProviderCredentialRequest(string Credential);

public sealed record ProviderTestResponse(
    bool Succeeded,
    string Message,
    ProviderStatusResponse Provider);
