namespace FamilyLibrarian.Domain.Conversation;

/// <summary>
/// One user's current conversation in one place (a Matrix room or the web page): the minimal structured state, never a
/// transcript. At most one row per (user, transport, scope).
/// </summary>
public sealed class StoredConversationSession
{
    public const int MaxStateLength = 4_000;

    private StoredConversationSession()
    {
    }

    public StoredConversationSession(Guid userId, string transport, string scopeKey, string stateJson, int turnCount, DateTimeOffset startedAtUtc, DateTimeOffset lastActivityAtUtc)
    {
        UserId = userId;
        Transport = transport;
        ScopeKey = scopeKey;
        Update(stateJson, turnCount, startedAtUtc, lastActivityAtUtc);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public string Transport { get; private set; } = null!;

    public string ScopeKey { get; private set; } = null!;

    public string StateJson { get; private set; } = null!;

    public int TurnCount { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset LastActivityAtUtc { get; private set; }

    public void Update(string stateJson, int turnCount, DateTimeOffset startedAtUtc, DateTimeOffset lastActivityAtUtc)
    {
        ArgumentNullException.ThrowIfNull(stateJson);
        if (stateJson.Length > MaxStateLength)
        {
            throw new ArgumentOutOfRangeException(nameof(stateJson), "Conversation state is bounded.");
        }

        StateJson = stateJson;
        TurnCount = turnCount;
        StartedAtUtc = startedAtUtc;
        LastActivityAtUtc = lastActivityAtUtc;
    }
}

/// <summary>
/// A message the conversation has already handled, keyed by (transport, scope, event id). Written in the same database
/// transaction as the effects it guards, so a crash leaves either both or neither. Holds the reply so a replay can answer
/// without redoing anything, and whether that reply was confirmed delivered.
/// </summary>
public sealed class ProcessedConversationEvent
{
    public const int MaxReplyLength = 8_000;

    private ProcessedConversationEvent()
    {
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public string Transport { get; private set; } = null!;

    public string ScopeKey { get; private set; } = null!;

    public string EventId { get; private set; } = null!;

    public string? ReplyJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? DeliveredAtUtc { get; private set; }
}

/// <summary>
/// A deterministic key derived from a message's identity that a conversation request claims exactly once. It outlives the
/// processed-event row, so a replay can never create a second request even if that row was pruned.
/// </summary>
public sealed class ConversationRequestKey
{
    private ConversationRequestKey()
    {
    }

    public string Key { get; private set; } = null!;

    public Guid UserId { get; private set; }

    public Guid? RequestId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }
}
