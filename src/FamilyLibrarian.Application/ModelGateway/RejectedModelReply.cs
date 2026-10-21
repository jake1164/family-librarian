using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.ModelGateway;

/// <summary>
/// A model reply that Family Librarian's own validation rejected, with just enough context to see why. Temporary
/// diagnostic: it is only produced when an operator turns capture on, and never for an exchange that carried book text.
/// </summary>
/// <param name="Reply">Single line, control characters removed, at most <see cref="MaxReplyCharacters"/> characters.</param>
/// <param name="Context">Caller-supplied identifiers only (for example evidence id, polarity and code). Never file text.</param>
public sealed record RejectedModelReply(
    ModelRole Role, ModelCallOutcome Outcome, ModelFailureReason? Reason, Guid? MediaAssetId, string Reply, string? Context)
{
    public const int MaxReplyCharacters = 2_000;
    public const int MaxContextCharacters = 4_000;
}

/// <summary>Where rejected replies go when capture is on. Off by default; a failing sink must never affect a model call.</summary>
public interface IRejectedModelReplySink
{
    bool IsEnabled { get; }

    void Record(RejectedModelReply reply);
}
