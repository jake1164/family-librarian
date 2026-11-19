namespace FamilyLibrarian.Contracts.Conversation;

/// <summary>
/// One message typed into the web chat. <paramref name="MessageId"/> is a client-generated id for this message: sending the
/// same id again (a retry after a dropped connection) returns the original answer instead of acting twice.
/// <paramref name="ListId"/> is sent only by a button, and only the one its own reply named: it says which list the button was drawn
/// for, so a press that arrives after a newer list replaced it does nothing. Typed text sends none.
/// </summary>
public sealed record ConversationMessageRequest(string Text, string? MessageId, string? ListId = null);

public sealed record ConversationChoiceResponse(int Number, string Title, string Authors, string? Detail);

public sealed record ConversationRequestSummaryResponse(
    Guid RequestId, string Title, string Authors, string Status, IReadOnlyList<string> Formats);

/// <summary>One line of an answer card: a record with the facts that line rests on, and a link only when the server built one.</summary>
public sealed record ConversationAnswerLineResponse(string Title, string? Authors, IReadOnlyList<string> Facts, string? Href);

public sealed record ConversationAnswerGroupResponse(string Heading, IReadOnlyList<ConversationAnswerLineResponse> Lines);

/// <summary>
/// A structured answer for the page. Every string is built on the server from validated data; <see cref="Href"/> is an internal
/// application path derived from a saved catalog id, never a provider value.
/// </summary>
public sealed record ConversationAnswerCardResponse(
    string Kind,
    string Heading,
    IReadOnlyList<ConversationAnswerLineResponse> Lines,
    IReadOnlyList<ConversationAnswerGroupResponse> Groups,
    IReadOnlyList<string> Notes,
    string? Href);

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
    bool Replayed,
    IReadOnlyList<ConversationAnswerCardResponse> Answers,
    string? ListId = null);

public sealed record ConversationStatusResponse(bool Enabled, int MaxMessageLength);
