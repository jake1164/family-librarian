using FamilyLibrarian.Application.Communications;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// Runs one inbound Matrix message through the conversation engine in a scope of its own. The scope matters: the principal is
/// established from the stored link for this one message and disappears with it, so concurrent messages from different people
/// can never see each other's identity.
/// </summary>
public interface IConversationTurnExecutor
{
    /// <summary>
    /// Authenticates the sender against the verified link and current account, then handles the message. Null means the
    /// message was not handled at all (not authenticated, no event id, or the feature is off): say nothing.
    /// </summary>
    Task<ConversationTurnResult?> RunMatrixTurnAsync(MatrixInboundMessage message, string text, CancellationToken cancellationToken);

    /// <summary>Records that a turn's reply was delivered, so a replay of the same event does not send it again.</summary>
    Task MarkDeliveredAsync(ConversationEventKey key, CancellationToken cancellationToken);
}
