namespace FamilyLibrarian.Contracts.Providers;

public sealed record ExternalProviderResponse(
    Guid Id,
    string ProviderId,
    string DisplayName,
    string BaseUrl,
    bool IsEnabled,
    string RecheckSchedule,
    bool AutoAcquireEnabled,
    int AutomaticAttemptLimit,
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
    DateTimeOffset? LastTestedAtUtc,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    IReadOnlyList<ExternalProviderHealthIssueResponse> CachedHealthIssues);

/// <summary>
/// A provider-reported reason behind a degraded/unavailable result. Untrusted,
/// plain text: render as text only. Admin-only (this response is only served
/// to administrators).
/// </summary>
/// <param name="Operation"><c>search</c>, <c>acquire</c>, or <c>general</c>.</param>
public sealed record ExternalProviderHealthIssueResponse(string Operation, string? Code, string Message);

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

/// <summary>
/// How many candidates unattended acquisition may download and fail to verify
/// for one requested format before it stops and waits for a librarian. Set to
/// 1 for a source with a limited download allowance.
/// </summary>
public sealed record SetExternalProviderAutomaticAttemptLimitRequest(int Limit);

public sealed record SetExternalProviderApiKeyRequest(string ApiKey);

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
