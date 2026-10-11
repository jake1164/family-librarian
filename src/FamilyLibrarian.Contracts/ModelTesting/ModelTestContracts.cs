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
    int MaxOutputTokens);

public sealed record SaveModelTestSettingsRequest(
    string? EndpointUrl,
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens);

public sealed record SetModelTestEnabledRequest(bool Enabled);

public sealed record SetModelTestApiKeyRequest(string ApiKey);

/// <param name="Target"><c>Primary</c>, <c>Comparison</c> or <c>Cloud</c>.</param>
/// <param name="Checks">Check names to run; empty or null runs all of them.</param>
/// <param name="ConfirmCloud">Must be true, in addition to the cloud setting, for a cloud model to be called.</param>
public sealed record RunModelTestRequest(string Target, IReadOnlyList<string>? Checks, bool ConfirmCloud);

public sealed record ModelCheckResultResponse(
    string Check,
    string Status,
    string Detail,
    int Calls,
    long LatencyMilliseconds,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    string? Sample);

/// <summary>What a run observed. Informational only.</summary>
public sealed record ModelTestReportResponse(
    string Target,
    bool IsCloud,
    string ConfiguredModel,
    IReadOnlyList<string> ReportedModels,
    IReadOnlyList<ModelCheckResultResponse> Results,
    int TotalCalls,
    int? TotalTokens,
    long TotalLatencyMilliseconds);
