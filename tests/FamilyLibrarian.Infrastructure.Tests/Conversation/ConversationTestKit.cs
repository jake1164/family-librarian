using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

internal sealed class FakeCurrentUser(Guid? userId) : ICurrentUser
{
    public Guid? UserId { get; set; } = userId;

    public string? DisplayName => UserId is null ? null : "Test User";
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class InMemorySessionStore : IConversationSessionStore
{
    public Dictionary<(Guid, string, string), ConversationSession> Sessions { get; } = [];

    private static ConversationSession Copy(ConversationSession session) => new(
        session.UserId, session.Transport, session.ScopeKey, ConversationState.Deserialize(session.State.Serialize()),
        session.TurnCount, session.StartedAtUtc, session.LastActivityAtUtc);

    public Task<ConversationSession?> FindAsync(Guid userId, string transport, string scopeKey, CancellationToken cancellationToken) =>
        Task.FromResult(Sessions.TryGetValue((userId, transport, scopeKey), out var found) ? Copy(found) : null);

    public Task SaveAsync(ConversationSession session, CancellationToken cancellationToken)
    {
        Sessions[(session.UserId, session.Transport, session.ScopeKey)] = Copy(session);
        return Task.CompletedTask;
    }

    public Dictionary<(Guid, string, string), ConversationSession> Snapshot() =>
        Sessions.ToDictionary(pair => pair.Key, pair => Copy(pair.Value));

    public void Restore(Dictionary<(Guid, string, string), ConversationSession> snapshot)
    {
        Sessions.Clear();
        foreach (var pair in snapshot) Sessions[pair.Key] = Copy(pair.Value);
    }
}

internal sealed class InMemoryEventLog : IConversationEventLog
{
    public Dictionary<(string, string, string), (Guid UserId, string? ReplyJson, DateTimeOffset? Delivered)> Events { get; } = [];

    public Dictionary<string, (Guid UserId, Guid? RequestId)> Keys { get; } = [];

    public Task<ConversationEventRecord?> FindAsync(string transport, string scopeKey, string eventId, CancellationToken cancellationToken) =>
        Task.FromResult(Events.TryGetValue((transport, scopeKey, eventId), out var found)
            ? new ConversationEventRecord(found.UserId, found.ReplyJson, found.Delivered)
            : null);

    public Task<bool> TryClaimAsync(Guid userId, string transport, string scopeKey, string eventId, CancellationToken cancellationToken)
    {
        if (Events.ContainsKey((transport, scopeKey, eventId))) return Task.FromResult(false);
        Events[(transport, scopeKey, eventId)] = (userId, null, null);
        return Task.FromResult(true);
    }

    public Task CompleteAsync(string transport, string scopeKey, string eventId, string replyJson, CancellationToken cancellationToken)
    {
        var current = Events[(transport, scopeKey, eventId)];
        Events[(transport, scopeKey, eventId)] = (current.UserId, replyJson, current.Delivered);
        return Task.CompletedTask;
    }

    public Task MarkDeliveredAsync(string transport, string scopeKey, string eventId, CancellationToken cancellationToken)
    {
        var current = Events[(transport, scopeKey, eventId)];
        Events[(transport, scopeKey, eventId)] = (current.UserId, current.ReplyJson, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task<(bool Claimed, Guid? ExistingRequestId)> TryClaimRequestKeyAsync(Guid userId, string requestKey, CancellationToken cancellationToken)
    {
        if (Keys.TryGetValue(requestKey, out var existing)) return Task.FromResult((false, existing.RequestId));
        Keys[requestKey] = (userId, null);
        return Task.FromResult<(bool, Guid?)>((true, null));
    }

    public Task AttachRequestAsync(string requestKey, Guid requestId, CancellationToken cancellationToken)
    {
        Keys[requestKey] = (Keys[requestKey].UserId, requestId);
        return Task.CompletedTask;
    }
}

/// <summary>Runs the operation once. Optionally fails after it to model a crash before commit (the fake rolls its stores back).</summary>
internal sealed class FakeTransaction(InMemorySessionStore sessions, InMemoryEventLog events) : IConversationTransaction
{
    public bool CrashBeforeCommit { get; set; }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var sessionSnapshot = sessions.Snapshot();
        var eventSnapshot = new Dictionary<(string, string, string), (Guid, string?, DateTimeOffset?)>(events.Events);
        var keySnapshot = new Dictionary<string, (Guid, Guid?)>(events.Keys);
        var result = await operation(cancellationToken);
        if (CrashBeforeCommit)
        {
            sessions.Restore(sessionSnapshot);
            events.Events.Clear();
            foreach (var pair in eventSnapshot) events.Events[pair.Key] = pair.Value;
            events.Keys.Clear();
            foreach (var pair in keySnapshot) events.Keys[pair.Key] = pair.Value;
            throw new InvalidOperationException("simulated crash before commit");
        }

        return result;
    }
}

internal sealed class FakeLimiter : IConversationLimiter
{
    public bool Deny { get; set; }

    public Task<IDisposable?> TryEnterAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IDisposable?>(Deny ? null : new Lease());

    private sealed class Lease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class FakeTools : IConversationTools
{
    public Dictionary<string, ConversationSearchResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = [];

    public List<(Guid WorkId, IReadOnlyList<RequestMediaType> Formats)> Created { get; } = [];

    public CreateBookRequestResult? NextCreateResult { get; set; }

    public bool ResolveFails { get; set; }

    public IReadOnlyList<BookRequestView> Requests { get; set; } = [];

    public Dictionary<string, Guid> WorkIds { get; } = [];

    public Dictionary<string, SeriesLookupResult> SeriesResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ConversationChoiceRef Choice(string id, string title, string authors = "Mara Quill", string? detail = "2021") =>
        new("fake", id, title, authors, detail);

    public Task<ConversationSearchResult> SearchBooksAsync(string text, int maxChoices, CancellationToken cancellationToken)
    {
        Calls.Add($"search:{text}");
        return Task.FromResult(Results.GetValueOrDefault(text) ?? new ConversationSearchResult([], false, []));
    }

    public Task<string?> GetBookDetailsAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
    {
        Calls.Add($"details:{choice.ExternalId}");
        return Task.FromResult<string?>($"{choice.Title} by {choice.Authors}\nA fictional description.");
    }

    public Task<IReadOnlyList<BookRequestView>> GetMyRequestsAsync(CancellationToken cancellationToken)
    {
        Calls.Add("status");
        return Task.FromResult(Requests);
    }

    public Task<ConversationLibraryResult?> CheckLibraryAsync(string text, CancellationToken cancellationToken)
    {
        Calls.Add($"library:{text}");
        return Task.FromResult<ConversationLibraryResult?>(new ConversationLibraryResult("Dune", "Frank Herbert", ["ebook"], false));
    }

    public Task<SeriesLookupResult> FindSeriesBooksAsync(string seriesName, decimal? position, int maxBooks, CancellationToken cancellationToken)
    {
        Calls.Add($"series:{seriesName}|{position?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"}");
        return Task.FromResult(SeriesResults.GetValueOrDefault(seriesName) ?? new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []));
    }

    public Task<Guid?> ResolveWorkAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
    {
        Calls.Add($"resolve:{choice.ExternalId}");
        if (ResolveFails) return Task.FromResult<Guid?>(null);
        if (!WorkIds.TryGetValue(choice.ExternalId, out var id)) WorkIds[choice.ExternalId] = id = Guid.NewGuid();
        return Task.FromResult<Guid?>(id);
    }

    public Task<CreateBookRequestResult> CreateBookRequestAsync(
        Guid workId, IReadOnlyList<RequestMediaType> formats, CancellationToken cancellationToken)
    {
        Calls.Add("create");
        Created.Add((workId, formats));
        return Task.FromResult(NextCreateResult ?? CreateBookRequestResult.Created(View(workId, "Created", formats)));
    }

    public static BookRequestView View(Guid workId, string title, IReadOnlyList<RequestMediaType> formats, RequestStatus status = RequestStatus.PendingAcquisition) =>
        new(Guid.NewGuid(), workId, title, ["Mara Quill"], null, status,
            formats.Select(format => new RequestFormatView(Guid.NewGuid(), format, RequestFormatStatus.Requested)).ToArray(),
            null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);
}

internal sealed class ConversationHarness
{
    public ConversationHarness(bool enabled = true, Guid? userId = null)
    {
        Options = new ConversationOptions { Enabled = enabled, MatrixEnabled = enabled, WebEnabled = enabled };
        User = new FakeCurrentUser(userId ?? Guid.NewGuid());
        Transaction = new FakeTransaction(Sessions, Events);
    }

    public ConversationOptions Options { get; }

    public FakeCurrentUser User { get; }

    public FakeClock Clock { get; } = new();

    public InMemorySessionStore Sessions { get; } = new();

    public InMemoryEventLog Events { get; } = new();

    public FakeTransaction Transaction { get; }

    public FakeLimiter Limiter { get; } = new();

    public FakeTools Tools { get; } = new();

    public IBookIntentInterpreter? Interpreter { get; set; }

    public ConversationService Service => new(User, Tools, Sessions, Events, Transaction, Limiter, Options, Clock, Interpreter);

    public int EventCounter { get; private set; }

    public string NextEventId() => $"$event{++EventCounter}";

    public Task<ConversationTurnResult> SayAsync(string text, string? eventId = null, string transport = ConversationTransports.Matrix, string scope = "!room:example.test") =>
        Service.HandleAsync(new ConversationTurn(transport, scope, eventId ?? NextEventId(), text), CancellationToken.None);

    /// <summary>Registers three fictional results for a query.</summary>
    public ConversationHarness WithResults(string query)
    {
        Tools.Results[query] = new ConversationSearchResult(
            [FakeTools.Choice("a", "The Lantern Road"), FakeTools.Choice("b", "The Lantern Road: Companion Guide", "Other Author"), FakeTools.Choice("c", "Lantern Road Stories")],
            false, []);
        return this;
    }
}
