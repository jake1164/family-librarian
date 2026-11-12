using System.Text.Json;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// Conversation settings. Everything is off until explicitly enabled, and the limits are the approved defaults (D-4).
/// Bound once at startup from the <c>Conversation</c> configuration section.
/// </summary>
public sealed class ConversationOptions
{
    /// <summary>Master switch. Nothing below does anything while this is false.</summary>
    public bool Enabled { get; set; }

    public bool MatrixEnabled { get; set; }

    public bool WebEnabled { get; set; }

    /// <summary>Turns in one session before the context is cleared (D-4: 12).</summary>
    public int MaxTurnsPerSession { get; set; } = 12;

    /// <summary>Minutes of silence after which the session context is cleared (D-4: 20).</summary>
    public int IdleExpiryMinutes { get; set; } = 20;

    /// <summary>Longest accepted message (D-4: 2,000).</summary>
    public int MaxInputCharacters { get; set; } = 2_000;

    /// <summary>Rolling per-user ceiling on messages, so deterministic commands are capped only by a rate limit.</summary>
    public int MaxMessagesPerMinute { get; set; } = 20;

    /// <summary>Numbered results offered per search.</summary>
    public int MaxChoices { get; set; } = 5;

    public bool IsEnabledFor(string transport) => Enabled && transport switch
    {
        ConversationTransports.Matrix => MatrixEnabled,
        ConversationTransports.Web => WebEnabled,
        _ => false
    };
}

public enum ConversationPhase
{
    Idle = 0,

    /// <summary>A numbered list was offered and the user has not picked one.</summary>
    ChoosingBook = 1,

    /// <summary>One book was picked and the user must say which format they want.</summary>
    ChoosingFormat = 2
}

/// <summary>Server-side reference behind one numbered choice. The user only ever sees its number.</summary>
public sealed record ConversationChoiceRef(string ProviderId, string ExternalId, string Title, string Authors, string? Detail);

/// <summary>
/// The minimal structured context of a conversation: what was last offered and what is pending. Deliberately not a
/// transcript. Small and bounded so it can be stored as one short value.
/// </summary>
public sealed class ConversationState
{
    public const int MaxSerializedLength = 4_000;
    public const int MaxFieldLength = 200;

    public ConversationPhase Phase { get; set; }

    public string? Query { get; set; }

    public List<ConversationChoiceRef> Choices { get; set; } = [];

    public int? SelectedIndex { get; set; }

    /// <summary>A format the user already stated ("... audiobook"), applied when they pick a book.</summary>
    public List<RequestMediaType> FormatHint { get; set; } = [];

    /// <summary>True when the user's wording was a request ("get ...") rather than a plain search.</summary>
    public bool RequestIntent { get; set; }

    public static ConversationState Empty() => new();

    public string Serialize()
    {
        var json = JsonSerializer.Serialize(this);
        return json.Length <= MaxSerializedLength ? json : JsonSerializer.Serialize(Empty());
    }

    public static ConversationState Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxSerializedLength)
        {
            return Empty();
        }

        try
        {
            return JsonSerializer.Deserialize<ConversationState>(json) ?? Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    public static string Clip(string? value, int max = MaxFieldLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}

/// <summary>A user's conversation in one place (a Matrix room or the web page), with when it started, last moved and expires.</summary>
public sealed class ConversationSession(
    Guid userId, string transport, string scopeKey, ConversationState state, int turnCount, DateTimeOffset startedAtUtc, DateTimeOffset lastActivityAtUtc)
{
    public Guid UserId { get; } = userId;

    public string Transport { get; } = transport;

    public string ScopeKey { get; } = scopeKey;

    public ConversationState State { get; set; } = state;

    public int TurnCount { get; set; } = turnCount;

    public DateTimeOffset StartedAtUtc { get; set; } = startedAtUtc;

    public DateTimeOffset LastActivityAtUtc { get; set; } = lastActivityAtUtc;
}

public interface IConversationSessionStore
{
    Task<ConversationSession?> FindAsync(Guid userId, string transport, string scopeKey, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces the single session for (user, transport, scope).</summary>
    Task SaveAsync(ConversationSession session, CancellationToken cancellationToken);
}

/// <summary>A recorded turn: who sent it, what we answered, and whether the answer was confirmed delivered.</summary>
public sealed record ConversationEventRecord(Guid UserId, string? ReplyJson, DateTimeOffset? DeliveredAtUtc);

/// <summary>
/// Durable processed-message and request-key records (alpha.6 D-3 binding addition: crash-safe idempotency). All writes made
/// during a turn's commit step happen inside one <see cref="IConversationTransaction"/>, so a crash leaves either the whole
/// turn applied or none of it.
/// </summary>
public interface IConversationEventLog
{
    Task<ConversationEventRecord?> FindAsync(string transport, string scopeKey, string eventId, CancellationToken cancellationToken);

    /// <summary>Records that the message is being handled. False when it was already recorded (a replay or a lost race).</summary>
    Task<bool> TryClaimAsync(Guid userId, string transport, string scopeKey, string eventId, CancellationToken cancellationToken);

    Task CompleteAsync(string transport, string scopeKey, string eventId, string replyJson, CancellationToken cancellationToken);

    Task MarkDeliveredAsync(string transport, string scopeKey, string eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Claims a deterministic request key derived from the message id. False when the key was already used, in which case
    /// the existing request id (if recorded) is returned.
    /// </summary>
    Task<(bool Claimed, Guid? ExistingRequestId)> TryClaimRequestKeyAsync(Guid userId, string requestKey, CancellationToken cancellationToken);

    Task AttachRequestAsync(string requestKey, Guid requestId, CancellationToken cancellationToken);
}

/// <summary>Runs the commit step of a turn atomically. Failure rolls back every write made inside.</summary>
public interface IConversationTransaction
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}

/// <summary>Per-user pacing: at most one turn in flight per user, and a rolling message ceiling.</summary>
public interface IConversationLimiter
{
    /// <summary>Returns a lease to dispose when the turn ends, or null when the user is over the limit or already mid-turn.</summary>
    Task<IDisposable?> TryEnterAsync(Guid userId, CancellationToken cancellationToken);
}
