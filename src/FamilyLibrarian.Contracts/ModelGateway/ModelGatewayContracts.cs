namespace FamilyLibrarian.Contracts.ModelGateway;

/// <param name="Role">A role name such as <c>CandidateAmbiguityAssessment</c>.</param>
/// <param name="Placement"><c>Local</c> or <c>Cloud</c>.</param>
/// <param name="Reasoning"><c>Inherit</c> (send nothing; the default) or <c>Disabled</c> (ask for no hidden reasoning).</param>
public sealed record ModelRoleDto(string Role, bool IsEnabled, string Placement, string? LocalModel, string? CloudModel, string Reasoning = "Inherit");

/// <summary>The gateway settings. The API key is never returned, only whether one is stored.</summary>
public sealed record ModelGatewaySettingsResponse(
    bool IsEnabled,
    string? EndpointUrl,
    bool HasApiKey,
    DateTimeOffset? ApiKeySetAtUtc,
    bool AllowCloud,
    IReadOnlyList<ModelRoleDto> Roles,
    ModelLimitsDto Limits,
    bool AllowCloudExcerpts = false,
    ModelConnectionNoticeDto? ConnectionNotice = null,
    bool AllowModelHoldRelease = false);

/// <summary>
/// Shown only when the older model-test connection and the shared one disagree. Contains no key; both are kept until an
/// administrator chooses.
/// </summary>
public sealed record ModelConnectionNoticeDto(
    bool HasConflict,
    string? SharedEndpointUrl,
    bool SharedHasApiKey,
    string? LegacyTestEndpointUrl,
    bool LegacyTestHasApiKey,
    string? Explanation);

/// <param name="Keep"><c>Shared</c> (discard the older test connection) or <c>LegacyTest</c> (adopt it; production AI is switched off until re-enabled).</param>
public sealed record ResolveModelConnectionRequest(string Keep);

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
    bool? AllowCloudExcerpts = null,
    bool? AllowModelHoldRelease = null);

/// <param name="ReportedTokens">Tokens the provider reported; calls it reported nothing for are counted in <paramref name="CallsWithoutUsage"/>, not as zero.</param>
/// <param name="Opportunities">Facts about the situation the calls were made in (for example a tie existed), never about what the model did.</param>
public sealed record ModelUsageRowResponse(
    string Role, string Budget, string Outcome, bool IsCloud, int Calls, long ReportedTokens, int CallsWithoutUsage,
    string Model = "", string? ReasonCode = null, IReadOnlyList<string>? WorkflowEffects = null, IReadOnlyList<string>? Opportunities = null);

public sealed record ModelRecommendationRowResponse(string Role, string Model, bool IsCloud, string Verdict, int Findings);

/// <param name="MedianMilliseconds">Nearest-rank median of finished calls; also <paramref name="P95Milliseconds"/>. Real latencies, never interpolated.</param>
public sealed record ModelLatencyRowResponse(
    string Role, string Model, bool IsCloud, int Calls, long MedianMilliseconds, long P95Milliseconds, long ReportedTokens);

/// <param name="Days">The window the rows cover: 1, 7, 30 or 90.</param>
public sealed record ModelUsageResponse(DateTimeOffset SinceUtc, IReadOnlyList<ModelUsageRowResponse> Rows,
    IReadOnlyList<ModelRecommendationRowResponse>? Recommendations = null, IReadOnlyList<ModelLatencyRowResponse>? Latency = null, int Days = 1);

/// <summary>One week of kept counts (after the 90-day ledger prune). Counts only; a week split across two prunes has an upper-bound p95.</summary>
public sealed record ModelUsageTrendRowResponse(
    DateTimeOffset WeekStartUtc, string Role, string Model, bool IsCloud, int Calls, int AcceptedReplies, int RejectedReplies, int ProviderErrors,
    int CancelledCalls, int ActionCalls, int NoChangeCalls, int AbstainedCalls, int AdvisoryCalls, long ReportedTokens, int CallsWithoutUsage,
    long LatencyMillisecondsSum, int LatencyCalls, long LatencyP95Milliseconds, int DownstreamPositive, int DownstreamNegative,
    IReadOnlyList<string> Opportunities);

public sealed record ModelUsageTrendResponse(int Weeks, IReadOnlyList<ModelUsageTrendRowResponse> Rows);

/// <summary>When a model's call changed something, what was observed afterwards. Decision counts, not a score: facts are partly influenced by the model's own note.</summary>
public sealed record AiEffectivenessRowResponse(
    string Role, int ActedCalls, int WithFacts, int PositiveOnly, int NegativeOnly, int Mixed, int InfluencedByNote);

public sealed record AiEffectivenessResponse(int Days, IReadOnlyList<AiEffectivenessRowResponse> Roles);

public sealed record ModelCallFailureResponse(
    Guid Id, string Role, string Model, bool IsCloud, string Outcome, string? ReasonCode, string Reason,
    DateTimeOffset OccurredAtUtc, long? LatencyMilliseconds, Guid? MediaAssetId = null, ModelCallArtifactResponse? Artifact = null);

public sealed record ModelCallArtifactResponse(Guid AssetId, Guid WorkId, string Format, Guid? BundleId, int? TrackNumber, int? TrackCount);

public sealed record ModelCallFailuresResponse(
    DateTimeOffset SinceUtc, DateTimeOffset UntilUtc, int Page, bool HasMore, IReadOnlyList<ModelCallFailureResponse> Rows);

public sealed record SetModelGatewayEnabledRequest(bool Enabled);

public sealed record SetModelGatewayApiKeyRequest(string ApiKey);

/// <summary>The models the endpoint says it serves. <paramref name="Problem"/> is set (and the list empty) when it could not be read; a name can always be typed.</summary>
public sealed record ModelListResponse(IReadOnlyList<string> Models, string? Problem);
