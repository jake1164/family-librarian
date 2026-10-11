namespace FamilyLibrarian.Contracts.ModelTesting;

/// <summary>The model test settings. The connection (endpoint and API key) is the shared one on the AI models page and is never returned here.</summary>
public sealed record ModelTestSettingsResponse(
    bool IsEnabled,
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens,
    string Reasoning = "Inherit");

public sealed record SaveModelTestSettingsRequest(
    string? PrimaryModel,
    string? ComparisonModel,
    string? CloudModel,
    bool AllowCloud,
    int TimeoutSeconds,
    int MaxOutputTokens,
    string Reasoning = "Inherit");

public sealed record SetModelTestEnabledRequest(bool Enabled);

/// <param name="Target"><c>Primary</c>, <c>Comparison</c>, <c>Cloud</c>, <c>Role</c> (a production role's configured model, read only) or <c>Alias</c> (a test-only alias).</param>
/// <param name="Checks">Check names to run; empty or null runs all of them.</param>
/// <param name="ConfirmCloud">Must be true, in addition to the cloud setting, for a cloud model to be called.</param>
/// <param name="Reasoning">This run only: <c>Inherit</c> or <c>Disabled</c>; null uses the saved test setting.</param>
/// <param name="TimeoutSeconds">This run only; null uses the saved test setting.</param>
/// <param name="MaxOutputTokens">This run only; null uses the saved test setting.</param>
/// <param name="Role">For <c>Role</c>: the role whose configured model to test (<c>IntentInterpretation</c>, ...).</param>
/// <param name="Alias">For <c>Alias</c>: a test-only model alias.</param>
/// <param name="AliasIsCloud">For <c>Alias</c>: required. Whether the alias runs in the cloud; a name is never assumed to be local.</param>
public sealed record RunModelTestRequest(
    string Target, IReadOnlyList<string>? Checks, bool ConfirmCloud,
    string? Reasoning = null, int? TimeoutSeconds = null, int? MaxOutputTokens = null,
    string? Role = null, string? Alias = null, bool? AliasIsCloud = null);

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
