namespace FamilyLibrarian.Contracts.Conversation;

/// <summary>
/// One message typed into the web chat. <paramref name="MessageId"/> is a client-generated id for this message: sending the
/// same id again (a retry after a dropped connection) returns the original answer instead of acting twice.
/// </summary>
public sealed record ConversationMessageRequest(string Text, string? MessageId);

public sealed record ConversationChoiceResponse(int Number, string Title, string Authors, string? Detail);

public sealed record ConversationRequestSummaryResponse(
    Guid RequestId, string Title, string Authors, string Status, IReadOnlyList<string> Formats);

/// <summary>
/// The chat's answer. <see cref="Text"/> is always complete on its own; the other parts let the page offer buttons that only
/// ever send ordinary messages back.
/// </summary>
public sealed record ConversationReplyResponse(
    string Kind,
    string Text,
    IReadOnlyList<ConversationChoiceResponse> Choices,
    IReadOnlyList<ConversationRequestSummaryResponse> Requests,
    IReadOnlyList<string> SuggestedReplies,
    Guid? RequestId,
    bool Replayed);

public sealed record ConversationStatusResponse(bool Enabled, int MaxMessageLength);
