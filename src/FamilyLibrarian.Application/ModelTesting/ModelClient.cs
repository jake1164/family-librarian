namespace FamilyLibrarian.Application.ModelTesting;

/// <summary>Where and how to reach one model for one call. Resolved fresh each time, since settings can change at any moment.</summary>
public sealed record ModelConnection(Uri Endpoint, string? ApiKey, string Model, TimeSpan Timeout);

public sealed record ModelChatMessage(string Role, string Content);

/// <summary>A tool the model is told about so its tool-calling support can be observed. It is never executed.</summary>
public sealed record ModelToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record ModelRequest(
    IReadOnlyList<ModelChatMessage> Messages,
    int MaxOutputTokens,
    bool JsonObjectOutput = false,
    IReadOnlyList<ModelToolDefinition>? Tools = null);

public enum ModelFailureKind
{
    None = 0,
    Unreachable,
    Timeout,
    Unauthorized,
    ModelUnavailable,
    RateLimited,
    BadRequest,
    ServerError,
    InvalidResponse
}

public sealed record ModelUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

/// <summary>A tool the model asked for. Only the name is kept: arguments are model output and are never acted on.</summary>
public sealed record ModelToolCall(string Name);

/// <param name="Content">The model's text. Untrusted data: validate it, never obey it.</param>
public sealed record ModelResponse(
    bool Succeeded,
    ModelFailureKind Failure,
    string? FailureDetail,
    string? Content,
    IReadOnlyList<ModelToolCall> ToolCalls,
    string? FinishReason,
    string? ReportedModel,
    ModelUsage? Usage,
    long LatencyMilliseconds)
{
    public static ModelResponse Failed(ModelFailureKind kind, string? detail, long latencyMilliseconds) =>
        new(false, kind, detail, null, [], null, null, null, latencyMilliseconds);
}

/// <summary>
/// The thin, provider-independent seam to a chat model (alpha.6 M3-5). One request, one response: no retries, no fallback to
/// another model, no streaming, and no execution of anything the model returns. A provider failure is a
/// <see cref="ModelResponse"/> with a <see cref="ModelFailureKind"/>, never an exception; only cancellation by the caller
/// propagates. The production gateway (roles, budgets, shadow decisions) is a separate, later piece of work.
/// </summary>
public interface IModelClient
{
    Task<ModelResponse> CompleteAsync(ModelConnection connection, ModelRequest request, CancellationToken cancellationToken);
}
