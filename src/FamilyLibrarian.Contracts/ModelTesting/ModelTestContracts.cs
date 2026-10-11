namespace FamilyLibrarian.Contracts.ModelTesting;

/// <summary>The model test settings. The API key is never returned, only whether one is stored.</summary>
public sealed record ModelTestSettingsResponse(
    bool IsEnabled,
    string? EndpointUrl,
    bool HasApiKey,
    DateTimeOffset? ApiKeySetAtUtc,
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens,
    string Reasoning = "Inherit");

public sealed record SaveModelTestSettingsRequest(
    string? EndpointUrl,
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens,
    string Reasoning = "Inherit");

public sealed record SetModelTestEnabledRequest(bool Enabled);

public sealed record SetModelTestApiKeyRequest(string ApiKey);

/// <param name="Target"><c>Primary</c>, <c>Comparison</c> or <c>Cloud</c>.</param>
/// <param name="Checks">Check names to run; empty or null runs all of them.</param>
/// <param name="ConfirmCloud">Must be true, in addition to the cloud setting, for a cloud model to be called.</param>
/// <param name="Reasoning">This run only: <c>Inherit</c> or <c>Disabled</c>; null uses the saved test setting.</param>
/// <param name="TimeoutSeconds">This run only; null uses the saved test setting.</param>
/// <param name="MaxOutputTokens">This run only; null uses the saved test setting.</param>
public sealed record RunModelTestRequest(
    string Target, IReadOnlyList<string>? Checks, bool ConfirmCloud,
    string? Reasoning = null, int? TimeoutSeconds = null, int? MaxOutputTokens = null);

public sealed record ModelCheckResultResponse(
    string Check,
    string Status,
    string Detail,
    int Calls,
    long LatencyMilliseconds,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    string? Sample,
    bool OutputLimitReached = false);

/// <summary>What a run observed. Informational only.</summary>
public sealed record ModelTestReportResponse(
    string Target,
    bool IsCloud,
    string ConfiguredModel,
    IReadOnlyList<string> ReportedModels,
    IReadOnlyList<ModelCheckResultResponse> Results,
    int TotalCalls,
    int? TotalTokens,
    long TotalLatencyMilliseconds,
    string Reasoning = "Inherit",
    int MaxOutputTokens = 0);
