namespace FamilyLibrarian.Contracts.Providers;

public sealed record ExternalProviderResponse(
    Guid Id,
    string ProviderId,
    string DisplayName,
    string BaseUrl,
    bool IsEnabled,
    string RecheckSchedule,
    bool AutoAcquireEnabled,
    string AcquisitionMode,
    bool HasApiKey,
    string? ApiKeyHint,
    DateTimeOffset? ApiKeySetAtUtc,
    string? CachedProtocolVersion,
    string? CachedCapabilities,
    string? CachedInstanceId,
    bool InstanceReplacedSincePreviousTest,
    string? CachedHealthStatus,
    string? CachedSearchOperationStatus,
    string? CachedAcquireOperationStatus,
    string? CachedManagementUrl,
    string? CachedDocumentationUrl,
    string CachedEgressPolicy,
    string? EgressPolicyOverride,
    string EffectiveEgressPolicy,
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage);

public sealed record CreateExternalProviderRequest(string ProviderId, string DisplayName, string BaseUrl);

public sealed record SetExternalProviderDetailsRequest(string DisplayName, string BaseUrl);

public sealed record SetExternalProviderEnabledRequest(bool Enabled);

/// <summary>One of <c>Manual</c>, <c>Daily</c>, or <c>Weekly</c>.</summary>
public sealed record SetExternalProviderRecheckScheduleRequest(string RecheckSchedule);

/// <summary>
/// A separate, explicit opt-in for unattended acquisition of a
/// high-confidence candidate found on a scheduled recheck — independent of
/// <see cref="SetExternalProviderRecheckScheduleRequest"/>, which only
/// controls how often this provider is checked, never whether a match found
/// that way may be fetched without review.
/// </summary>
public sealed record SetExternalProviderAutoAcquireEnabledRequest(bool Enabled);

/// <summary>One of <c>SubscriptionFirst</c>, <c>FreeFirst</c>, <c>SubscriptionOnly</c>, or <c>FreeOnly</c>.</summary>
public sealed record SetExternalProviderAcquisitionModeRequest(string AcquisitionMode);

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
