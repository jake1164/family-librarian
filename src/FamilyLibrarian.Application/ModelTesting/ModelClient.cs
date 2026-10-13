using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelTesting;

/// <summary>Where and how to reach one model for one call. Resolved fresh each time, since settings can change at any moment.</summary>
public sealed record ModelConnection(Uri Endpoint, string? ApiKey, string Model, TimeSpan Timeout);

/// <summary>An image sent to a vision-capable model. The bytes were already checked by the caller; this seam only carries them.</summary>
public sealed record ModelImage(string MimeType, byte[] Bytes);

public sealed record ModelChatMessage(string Role, string Content, IReadOnlyList<ModelImage>? Images = null);

/// <summary>A tool the model is told about so its tool-calling support can be observed. It is never executed.</summary>
public sealed record ModelToolDefinition(string Name, string Description, string ParametersJsonSchema);

public sealed record ModelRequest(
    IReadOnlyList<ModelChatMessage> Messages,
    int MaxOutputTokens,
    bool JsonObjectOutput = false,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    ModelReasoningMode Reasoning = ModelReasoningMode.Inherit);

/// <summary>The connection the test harness should use. The key is a secret: never log, audit or return it.</summary>
/// <param name="IsLegacyConnection">True when an unresolved disagreement means the harness is still using its own older connection.</param>
public sealed record ModelTestConnectionInfo(Uri? Endpoint, string? ApiKey, bool IsLegacyConnection, string? Problem);

/// <summary>A production role's configured alias and placement, read only so an administrator can test exactly that model.</summary>
public sealed record ModelTestRoleTarget(ModelRole Role, string Alias, bool IsCloud);

/// <summary>
/// The test harness's read-only window onto the production gateway: it can look up the shared connection and a role's model,
/// and nothing else. There is deliberately no member that changes a role, a switch, a limit or a permission.
/// </summary>
public interface IModelTestTargetSource
{
    Task<ModelTestConnectionInfo> GetConnectionAsync(CancellationToken cancellationToken);

    Task<ModelTestRoleTarget?> FindRoleTargetAsync(ModelRole role, CancellationToken cancellationToken);
}

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
    InvalidResponse,

    /// <summary>The model or gateway rejected a feature of the request (JSON mode, tools, image input, or the reasoning setting).</summary>
    UnsupportedCapability
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
    /// <summary>
    /// True only when the provider said it stopped at the output cap (finish reason <c>length</c>). A missing or other finish
    /// reason is not evidence of truncation, so this stays false and an empty reply stays "empty".
    /// </summary>
    public bool IsOutputLimited =>
        FinishReason is not null && (FinishReason.Equals("length", StringComparison.OrdinalIgnoreCase) || FinishReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase));

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

/// <summary>The model names a gateway says it serves. Names only: nothing else about a model is trusted or kept.</summary>
public sealed record ModelListResult(bool Succeeded, ModelFailureKind Failure, string? FailureDetail, IReadOnlyList<string> ModelIds)
{
    public static ModelListResult Failed(ModelFailureKind kind, string? detail) => new(false, kind, detail, []);
}

/// <summary>
/// Asks an OpenAI-compatible endpoint which models it serves (<c>GET /v1/models</c>) so an administrator can choose one from a
/// list. One request, no retries and no redirects. It is a convenience only: a model that is not listed can still be typed.
/// </summary>
public interface IModelCatalogClient
{
    Task<ModelListResult> ListModelsAsync(Uri endpoint, string? apiKey, TimeSpan timeout, CancellationToken cancellationToken);
}
