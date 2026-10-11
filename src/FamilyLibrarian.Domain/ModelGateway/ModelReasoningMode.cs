namespace FamilyLibrarian.Domain.ModelGateway;

/// <summary>
/// Whether a request asks the model to skip its hidden "thinking" step. Persisted as text; additive only.
/// </summary>
/// <remarks>
/// Thinking models spend output tokens on reasoning before the visible answer, and those tokens count against the output
/// cap, so a tight cap can leave an empty reply. <see cref="Inherit"/> sends nothing and keeps whatever the provider does by
/// default. <see cref="Disabled"/> is an explicit opt-in for a model that is known to accept it; it is never sent on
/// the strength of a model's name, and a provider that rejects it is reported, not retried without it.
/// </remarks>
public enum ModelReasoningMode
{
    /// <summary>Send no reasoning parameter. Existing provider behaviour is unchanged.</summary>
    Inherit = 0,

    /// <summary>Ask for no reasoning (<c>reasoning_effort: "none"</c>, verified against LiteLLM with a local Qwen 3.5 alias).</summary>
    Disabled = 1
}
