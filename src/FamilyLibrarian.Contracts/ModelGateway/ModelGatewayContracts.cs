namespace FamilyLibrarian.Contracts.ModelGateway;

/// <param name="Role">A role name such as <c>CandidateAmbiguityAssessment</c>.</param>
/// <param name="Placement"><c>Local</c> or <c>Cloud</c>.</param>
public sealed record ModelRoleDto(string Role, bool IsEnabled, string Placement, string? LocalModel, string? CloudModel);

/// <summary>The gateway settings. The API key is never returned, only whether one is stored.</summary>
public sealed record ModelGatewaySettingsResponse(
    bool IsEnabled,
    string? EndpointUrl,
    bool HasApiKey,
    DateTimeOffset? ApiKeySetAtUtc,
    bool AllowCloud,
    IReadOnlyList<ModelRoleDto> Roles,
    ModelLimitsDto Limits,
    bool AllowCloudExcerpts = false);

/// <summary>A daily allowance for one kind of use. Zero stops all calls of that kind; it never means unlimited.</summary>
public sealed record ModelBudgetDto(int UserDailyRequests, int UserDailyTokens, int InstallationDailyRequests, int InstallationDailyTokens);

public sealed record ModelLimitsDto(
    int TimeoutSeconds,
    int MaxInputCharacters,
    int MaxOutputTokens,
    int MaxConcurrentCalls,
    ModelBudgetDto Interactive,
    ModelBudgetDto Background,
    int CloudDailyRequests,
    int CloudDailyTokens);

/// <param name="Limits">The new limits, or null to leave them unchanged.</param>
/// <param name="AllowCloudExcerpts">
/// Separate opt-in to send details read from downloaded files to a cloud model; null leaves it unchanged. Needs
/// <paramref name="AllowCloud"/>.
/// </param>
public sealed record SaveModelGatewaySettingsRequest(
    string? EndpointUrl, bool AllowCloud, IReadOnlyList<ModelRoleDto> Roles, ModelLimitsDto? Limits = null,
    bool? AllowCloudExcerpts = null);

/// <param name="ReportedTokens">Tokens the provider reported; calls it reported nothing for are counted in <paramref name="CallsWithoutUsage"/>, not as zero.</param>
public sealed record ModelUsageRowResponse(
    string Role, string Budget, string Outcome, bool IsCloud, int Calls, long ReportedTokens, int CallsWithoutUsage);

public sealed record ModelUsageResponse(DateTimeOffset SinceUtc, IReadOnlyList<ModelUsageRowResponse> Rows);

public sealed record SetModelGatewayEnabledRequest(bool Enabled);

public sealed record SetModelGatewayApiKeyRequest(string ApiKey);
