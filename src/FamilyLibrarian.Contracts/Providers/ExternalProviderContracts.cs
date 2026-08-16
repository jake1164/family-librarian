namespace FamilyLibrarian.Contracts.Providers;

public sealed record ExternalProviderResponse(
    Guid Id,
    string ProviderId,
    string DisplayName,
    string BaseUrl,
    bool IsEnabled,
    bool HasApiKey,
    string? ApiKeyHint,
    DateTimeOffset? ApiKeySetAtUtc,
    string? CachedProtocolVersion,
    string? CachedCapabilities,
    string CachedEgressPolicy,
    string? EgressPolicyOverride,
    string EffectiveEgressPolicy,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record CreateExternalProviderRequest(string ProviderId, string DisplayName, string BaseUrl);

public sealed record SetExternalProviderDetailsRequest(string DisplayName, string BaseUrl);

public sealed record SetExternalProviderEnabledRequest(bool Enabled);

public sealed record SetExternalProviderApiKeyRequest(string ApiKey);

/// <summary>One of "Normal", "PrivateRequired", "CustomProxy", or <c>null</c> to clear the override.</summary>
public sealed record SetExternalProviderEgressPolicyOverrideRequest(string? EgressPolicy);

public sealed record PrivateEgressGatewayResponse(
    bool IsEnabled,
    string? GatewayEndpoint,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record SetPrivateEgressGatewayEnabledRequest(bool Enabled);

public sealed record SetPrivateEgressGatewayEndpointRequest(string? GatewayEndpoint);

public sealed record ProviderCatalogEntryResponse(
    string Id,
    string Name,
    string? ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    string? License,
    string? Publisher,
    string? TrustLabel,
    string? OciImageDigest,
    string? HomepageUrl,
    string? Description);

public sealed record ProviderCatalogResponse(
    Guid Id,
    string Url,
    string DisplayName,
    bool IsEnabled,
    IReadOnlyList<ProviderCatalogEntryResponse> Entries,
    DateTimeOffset? LastFetchedAtUtc,
    bool? LastFetchSucceeded,
    string? LastFetchMessage);

public sealed record AddProviderCatalogRequest(string Url, string? DisplayName);

public sealed record SetProviderCatalogEnabledRequest(bool Enabled);
