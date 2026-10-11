namespace FamilyLibrarian.Application.Conversation;

/// <summary>Names of the transports that can carry a conversation. A transport only translates identity and text.</summary>
public static class ConversationTransports
{
    public const string Matrix = "matrix";
    public const string Web = "web";

    public static bool IsKnown(string? transport) => transport is Matrix or Web;
}

/// <summary>
/// One user message, already tied to a transport. <paramref name="ScopeKey"/> is the Matrix room or the web channel the
/// conversation lives in; <paramref name="EventId"/> is the transport's id for this message (the Matrix event id, or a
/// client-generated id on the web) and is what makes a replayed message harmless. It is never an identity.
/// </summary>
public sealed record ConversationTurn(string Transport, string ScopeKey, string? EventId, string Text);

public enum ConversationReplyKind
{
    Text,
    Help,
    Choices,
    Details,
    AskFormat,
    RequestCreated,
    RequestExists,
    Library,
    Status,
    Cancelled,
    OutOfDomain,
    NotUnderstood,
    Unauthenticated,
    Throttled,
    TooLong,
    Disabled,
    Unavailable
}

/// <summary>A numbered option the user can pick by number. Display text only; the real reference stays in server-side state.</summary>
public sealed record ConversationChoice(int Number, string Title, string Authors, string? Detail);

/// <summary>A request as the requester may see it. No other user's data and no internal ids beyond the request's own.</summary>
public sealed record ConversationRequestSummary(Guid RequestId, string Title, string Authors, string Status, IReadOnlyList<string> Formats);

/// <summary>
/// What the conversation says back. Plain text is always present so any transport can show it; the structured parts let a
/// richer transport (the web page) offer cards and buttons that only ever send ordinary messages back.
/// </summary>
public sealed record ConversationReply(
    ConversationReplyKind Kind,
    string Text,
    IReadOnlyList<ConversationChoice> Choices,
    IReadOnlyList<ConversationRequestSummary> Requests,
    IReadOnlyList<string> SuggestedReplies,
    Guid? RequestId = null)
{
    public static ConversationReply Of(ConversationReplyKind kind, string text, params string[] suggestedReplies) =>
        new(kind, text, [], [], suggestedReplies);
}

/// <summary>The outcome of handling one turn, including whether it was a replay and whether its reply still needs delivering.</summary>
/// <param name="DeliveryPending">
/// For a replayed turn: the original reply was recorded but never confirmed as delivered, so the adapter should send it
/// again. Always true for a first-time turn until the adapter reports delivery.
/// </param>
public sealed record ConversationTurnResult(ConversationReply Reply, bool Replayed, bool DeliveryPending, ConversationEventKey? EventKey);

/// <summary>Identifies a recorded turn so an adapter can report that its reply was delivered.</summary>
public sealed record ConversationEventKey(string Transport, string ScopeKey, string EventId);
