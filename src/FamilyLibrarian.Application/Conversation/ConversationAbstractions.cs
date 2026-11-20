using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;
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

    /// <summary>Turns in one session before the context is cleared (D-4 was 12; raised to 24 in CHAT-ANSWER-1 at the owner's request).</summary>
    public int MaxTurnsPerSession { get; set; } = 24;

    /// <summary>Minutes of silence after which the session context is cleared (D-4: 20).</summary>
    public int IdleExpiryMinutes { get; set; } = 20;

    /// <summary>Longest accepted message (D-4: 2,000).</summary>
    public int MaxInputCharacters { get; set; } = 2_000;

    /// <summary>
    /// Rolling per-user ceiling on messages, so deterministic commands are capped only by a rate limit. Cancel, help, status and
    /// empty messages do not count. Set with <c>Conversation__MaxMessagesPerMinute</c>. Raised from 20 to 120 (CHAT-ANSWER-1) because
    /// 20 was reachable by one person testing; the outbound catalog lookups have their own rate limiting.
    /// </summary>
    public int MaxMessagesPerMinute { get; set; } = 120;

    /// <summary>Numbered results offered per search.</summary>
    public int MaxChoices { get; set; } = 5;

    /// <summary>
    /// How many numbered books of a series the catalog must show before chat will say which one is the latest. Fewer than this is
    /// too little to name a "highest" without misleading: a single sampled companion or an early book would be offered as the
    /// newest. Below it, chat says how little it can see and searches the series name instead.
    /// </summary>
    /// <summary>
    /// Minimum numbered books a series listing must show before chat will name one as the latest.
    /// </summary>
    /// <remarks>
    /// No longer consulted: the shared answer contract decides whether a result may be called latest from the evidence and the
    /// coverage it actually has, so a thin sample is qualified rather than thresholded. Kept so an existing configuration value
    /// still binds without error.
    /// </remarks>
    public int MinKnownPositionsForLatest { get; set; } = 3;

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
    ChoosingFormat = 2,

    /// <summary>A bibliographic question needs a subject picked (a series or an author) before it can be answered.</summary>
    ChoosingSubject = 3,

    /// <summary>The books state more than one ordering, so the user must say which one the question is about.</summary>
    ChoosingOrder = 4
}

/// <summary>
/// Server-side reference behind one numbered choice. The user only ever sees its number.
/// <paramref name="SeriesName"/> is the series the catalog attached to this book, if any, so a follow-up such as "what comes next"
/// can ask about that series instead of guessing it from the title. <paramref name="SavedWorkId"/> is set when the record is
/// already in this library's catalog: an action can then use that identity instead of resolving the provider record again.
/// Both are optional, so state saved before they existed still loads.
/// </summary>
public sealed record ConversationChoiceRef(
    string ProviderId, string ExternalId, string Title, string Authors, string? Detail, string? SeriesName = null, Guid? SavedWorkId = null);

/// <summary>
/// One subject a bibliographic answer offered, kept so a later number can select it. It holds identifiers only — never evidence,
/// never a name as identity — and it is rebuilt into a <see cref="CatalogSubjectRef"/> when it is used, so a corrupt or
/// legacy entry selects nothing rather than something else.
/// </summary>
public sealed record ConversationSubjectChoice(
    CatalogSubjectKind Kind,
    string Label,
    Guid? SavedId = null,
    string? ProviderId = null,
    string? ExternalId = null,
    ExternalReferenceEntityType? NativeKind = null,
    SeriesOrderingKind Order = SeriesOrderingKind.Unspecified,
    string? Disambiguation = null)
{
    public const int MaxLabelLength = 200;

    /// <summary>Native ids longer than this cannot be kept in the bounded state: such a choice is not offered for later selection.</summary>
    public const int MaxNativeIdLength = 120;

    public static ConversationSubjectChoice? From(CatalogAnswerChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var reference = choice.Reference;
        // The state is bounded, and a cut id would name a different record: an id that does not fit is dropped, never shortened.
        if (reference.Native?.ExternalId is { Length: > MaxNativeIdLength }) return null;
        return new ConversationSubjectChoice(
            reference.Kind,
            ConversationState.Clip(reference.Label, MaxLabelLength),
            reference.SavedId,
            reference.Native?.ProviderId,
            reference.Native?.ExternalId,
            reference.Native?.Kind,
            reference.Order,
            choice.Disambiguation is { Length: > 0 } detail ? ConversationState.Clip(detail) : null);
    }

    /// <summary>The reference this choice stands for, or null when it no longer describes a usable subject.</summary>
    public CatalogSubjectRef? ToReference()
    {
        try
        {
            if (Kind == CatalogSubjectKind.Title) return CatalogSubjectRef.Title(Label);
            if (SavedId is { } saved && saved != Guid.Empty) return CatalogSubjectRef.Saved(Kind, Label, saved, Order);
            if (NativeKind is { } kind && SourceRef.TryCreate(ProviderId, ExternalId, kind, out var native))
            {
                return CatalogSubjectRef.FromSource(Kind, Label, native!, Order);
            }

            return null;
        }
        catch (ArgumentException)
        {
            // A label that cannot be kept whole, or a kind that does not fit: nothing is selected.
            return null;
        }
    }
}

/// <summary>
/// The minimal structured context of a conversation: what was last offered and what is pending. Deliberately not a
/// transcript. Small and bounded so it can be stored as one short value.
/// </summary>
public sealed class ConversationState
{
    public const int MaxSerializedLength = 4_000;
    public const int MaxFieldLength = 200;
    public const int MaxSubjectChoices = 6;

    /// <summary>The state format this build writes. <see cref="CurrentVersion"/> 2 added the retained answer reference; older values load as before.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public ConversationPhase Phase { get; set; }

    public string? Query { get; set; }

    public List<ConversationChoiceRef> Choices { get; set; } = [];

    public int? SelectedIndex { get; set; }

    /// <summary>A format the user already stated ("... audiobook"), applied when they pick a book.</summary>
    public List<RequestMediaType> FormatHint { get; set; } = [];

    /// <summary>True when the user's wording was a request ("get ...") rather than a plain search.</summary>
    public bool RequestIntent { get; set; }

    /// <summary>The question being answered, kept so a subject choice or a follow-up continues the same question.</summary>
    public CatalogAnswerOperation? AnswerOperation { get; set; }

    /// <summary>The position asked for (a position question) or the anchor position of a next-in-order question.</summary>
    public decimal? AnswerPosition { get; set; }

    /// <summary>The retained answer context reference as JSON; bounded by the answer layer and never cut.</summary>
    public string? AnswerContext { get; set; }

    /// <summary>The subjects offered for a clarification, in the order the user was shown them.</summary>
    public List<ConversationSubjectChoice> SubjectChoices { get; set; } = [];

    /// <summary>The orderings offered for a clarification, in the order the user was shown them.</summary>
    public List<SeriesOrderingKind> Orders { get; set; } = [];

    /// <summary>The single record the conversation is about, so "what comes next" and "is it available" refer to it.</summary>
    public ConversationSubjectChoice? FocusEntry { get; set; }

    /// <summary>
    /// The answer's records in the order they were numbered, so "editions 2" names the same record as line 2. An entry whose
    /// reference cannot be kept whole is null here and simply cannot be expanded.
    /// </summary>
    public List<ConversationSubjectChoice?> EntryChoices { get; set; } = [];

    /// <summary>
    /// Names the list the reader was last shown, so a button drawn for an earlier list can be told from one for this list. It is
    /// kept through details, availability, a pick and the format step, and replaced only when a new list is offered.
    /// </summary>
    public string? ListId { get; set; }

    /// <summary>The numbered items are series (an author's series), so a number selects a series rather than a book.</summary>
    public bool PicksFromSeries { get; set; }

    /// <summary>How many records the retained answer holds that have not been shown yet. "More" shows them with no provider read.</summary>
    public int MoreEntries { get; set; }

    /// <summary>The server-issued token for one more provider page of the same read, or null when the source has no more.</summary>
    public string? ContinuationToken { get; set; }

    /// <summary>Only novels were asked for ("latest novel in ..."), so the same filter continues on a follow-up.</summary>
    public bool AnswerNovelOnly { get; set; }

    /// <summary>Only main-series entries were asked for ("main series"), so the same filter continues on a follow-up.</summary>
    public bool AnswerMainOnly { get; set; }

    public static ConversationState Empty() => new();

    /// <summary>The same list with no further page: nothing more is held, and nothing is asked for again.</summary>
    public ConversationState WithoutMore()
    {
        var copy = (ConversationState)MemberwiseClone();
        copy.MoreEntries = 0;
        copy.ContinuationToken = null;
        return copy;
    }

    /// <summary>
    /// The state as one bounded value. Too large to keep whole, it drops the bulk in a fixed order (the numbered book list, then the
    /// subject choices, then the retained answer reference) rather than resetting the conversation silently: the next turn then says
    /// it has nothing to pick from, which is a recovery the reader can act on.
    /// </summary>
    public string Serialize()
    {
        var state = this;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var json = JsonSerializer.Serialize(state);
            if (json.Length <= MaxSerializedLength) return json;
            state = state.WithoutBulk();
        }

        return JsonSerializer.Serialize(Empty());
    }

    /// <summary>
    /// Whether the state is kept whole. A list offered to the reader is only offered as far as this holds, so a number that was shown
    /// is always one the next turn can act on, instead of the bulk being dropped silently after the reply was already sent.
    /// </summary>
    public bool FitsWhole() => JsonSerializer.Serialize(this).Length <= MaxSerializedLength;

    public static ConversationState Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxSerializedLength)
        {
            return Empty();
        }

        try
        {
            var state = JsonSerializer.Deserialize<ConversationState>(json) ?? Empty();
            // A state written by a newer build cannot be read safely: the conversation restarts rather than acting on a partial list.
            if (state.Version > CurrentVersion) return Empty();
            state.Version = CurrentVersion;
            return state;
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    private ConversationState WithoutBulk()
    {
        var copy = (ConversationState)MemberwiseClone();
        if (copy.Choices.Count > 0) copy.Choices = [];
        else if (copy.SubjectChoices.Count > 0) copy.SubjectChoices = [];
        else if (copy.AnswerContext is not null) copy.AnswerContext = null;
        else return Empty();
        return copy;
    }

    public static string Clip(string? value, int max = MaxFieldLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>
    /// Display text cut to a length with a visible mark, so a shortened title is never mistaken for the whole one. It is for what is
    /// shown and kept as a label only: identity always comes from the record's own id. A blank value is replaced by
    /// <paramref name="whenBlank"/>, so a label that must not be blank never is.
    /// </summary>
    public static string Ellipsize(string? value, int max, string whenBlank)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return whenBlank;
        if (trimmed.Length <= max) return trimmed;
        var cut = max - 1;
        if (char.IsHighSurrogate(trimmed[cut - 1])) cut--;
        return trimmed[..cut].TrimEnd() + "…";
    }
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

    /// <summary>
    /// Gives back a key claimed in this turn when the turn then refused to write, so the key never outlives a request that was not made.
    /// A key that already has a request attached is left alone.
    /// </summary>
    Task ReleaseRequestKeyAsync(string requestKey, CancellationToken cancellationToken);
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
    /// <param name="countsTowardLimit">
    /// False for control messages (cancel, help, status, empty) that do no catalog or model work: they neither use up nor are blocked
    /// by the rolling allowance, so a user over the limit can still cancel. They still take their turn, one at a time per user.
    /// </param>
    Task<IDisposable?> TryEnterAsync(Guid userId, CancellationToken cancellationToken, bool countsTowardLimit = true);
}
