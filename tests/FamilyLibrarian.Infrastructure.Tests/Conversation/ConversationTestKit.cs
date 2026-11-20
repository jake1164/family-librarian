using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Catalog;
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

    /// <summary>Runs while a request key is being claimed, so a test can change the world during that await.</summary>
    public Action? OnClaimKey { get; set; }

    public Task<(bool Claimed, Guid? ExistingRequestId)> TryClaimRequestKeyAsync(Guid userId, string requestKey, CancellationToken cancellationToken)
    {
        OnClaimKey?.Invoke();
        if (Keys.TryGetValue(requestKey, out var existing)) return Task.FromResult((false, existing.RequestId));
        Keys[requestKey] = (userId, null);
        return Task.FromResult<(bool, Guid?)>((true, null));
    }

    public Task AttachRequestAsync(string requestKey, Guid requestId, CancellationToken cancellationToken)
    {
        Keys[requestKey] = (Keys[requestKey].UserId, requestId);
        return Task.CompletedTask;
    }

    public Task ReleaseRequestKeyAsync(string requestKey, CancellationToken cancellationToken)
    {
        if (Keys.TryGetValue(requestKey, out var key) && key.RequestId is null) Keys.Remove(requestKey);
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

    /// <summary>One entry per message: whether the service asked for it to count against the rate limit.</summary>
    public List<bool> Counted { get; } = [];

    public Task<IDisposable?> TryEnterAsync(Guid userId, CancellationToken cancellationToken, bool countsTowardLimit = true)
    {
        Counted.Add(countsTowardLimit);
        return Task.FromResult<IDisposable?>(Deny ? null : new Lease());
    }

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

    public ConversationLibraryResult? NextLibraryResult { get; set; }

    public Task<ConversationLibraryResult?> CheckLibraryAsync(string text, CancellationToken cancellationToken)
    {
        Calls.Add($"library:{text}");
        return Task.FromResult<ConversationLibraryResult?>(NextLibraryResult ?? new ConversationLibraryResult("Dune", "Frank Herbert", ["ebook"], false));
    }

    public Task<ConversationLibraryResult?> CheckLibraryForChoiceAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
    {
        Calls.Add($"library-choice:{choice.ExternalId}");
        return Task.FromResult<ConversationLibraryResult?>(
            NextLibraryResult ?? new ConversationLibraryResult(choice.Title, choice.Authors, ["ebook"], false));
    }

    public Task<SeriesLookupResult> FindSeriesBooksAsync(string seriesName, decimal? position, string? afterTitle, int maxBooks, CancellationToken cancellationToken)
    {
        Calls.Add($"series:{seriesName}|{position?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all"}" + (afterTitle is null ? "" : $"|after:{afterTitle}"));
        return Task.FromResult(SeriesResults.GetValueOrDefault(seriesName) ?? new SeriesLookupResult(SeriesLookupKind.NotFound, null, [], [], []));
    }

    /// <summary>Runs while a work is being resolved, so a test can change the world between the engine's awaits.</summary>
    public Action? OnResolve { get; set; }

    public Task<Guid?> ResolveWorkAsync(ConversationChoiceRef choice, CancellationToken cancellationToken)
    {
        Calls.Add($"resolve:{choice.ExternalId}");
        OnResolve?.Invoke();
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

internal sealed class FakeAnswers : ICatalogAnswerService
{
    public List<CatalogAnswerRequest> Requests { get; } = [];

    /// <summary>Answers the scripted replies; null returns a plain clarification with nothing to choose from.</summary>
    public Func<CatalogAnswerRequest, CatalogAnswer>? Script { get; set; }

    public CatalogEditionsAnswer? EditionsAnswer { get; set; }

    /// <summary>The order the retained window keeps the series in, when it differs from the order the answer shows them (the real service sorts one and retains the other).</summary>
    public Func<CatalogAnswer, IReadOnlyList<CatalogAnswerSeries>>? RetainedSeries { get; set; }

    /// <summary>The same for offered choices.</summary>
    public Func<CatalogAnswer, IReadOnlyList<CatalogAnswerChoice>>? RetainedChoices { get; set; }

    public List<(Guid RootId, CatalogSubjectRef Entry)> EditionRequests { get; } = [];

    /// <summary>The store the offered choices are retained in, exactly as the real service retains them.</summary>
    public CatalogAnswerContextStore Store { get; set; } = new();

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UnixEpoch;

    public Task<CatalogAnswer> AnswerAsync(CatalogAnswerCaller caller, CatalogAnswerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Requests.Add(request);
        var answer = Script?.Invoke(request) ?? new CatalogAnswer
        {
            // A name that matched no subject, which is what the shared service reports and what chat's labelled search fallback
            // is allowed to answer.
            Operation = request.Operation,
            Outcome = CatalogAnswerOutcome.NoVerifiedMatch,
            Reasons = [CatalogAnswerCodes.NoChoicesFound],
            Today = DateOnly.FromDateTime(Now().UtcDateTime)
        };

        // The real service always answers the operation it was asked; the fake is stamped the same way so routing is faithful.
        answer = answer with { Operation = request.Operation, Position = answer.Position ?? request.Position };

        // The real service retains what it read and issues a reference; the fake does the same so follow-ups and picks are real.
        if (answer.Context is null && answer.Outcome != CatalogAnswerOutcome.ContextExpired && Store.CreateRoot(caller, Now()) is { } root)
        {
            Store.Retain(
                caller, root.RootId,
                new CatalogRetainedWindow(
                    request.Operation, answer.Subject, request.Order, answer.Entries, RetainedSeries?.Invoke(answer) ?? answer.Series,
                    RetainedChoices?.Invoke(answer) ?? answer.Choices, false, false, false,
                    false, [], "test-window", "fake", answer.ContinuationToken, Now(), CatalogReadKind.SeriesMembers),
                Now());
            answer = answer with { Context = new CatalogAnswerContextRef(root.RootId, request.Operation, answer.Subject, request.Anchor, request.Order) };
        }

        return Task.FromResult(answer);
    }

    public Task<CatalogEditionsAnswer> EditionsAsync(
        CatalogAnswerCaller caller, Guid contextRootId, CatalogSubjectRef entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        EditionRequests.Add((contextRootId, entry));
        return Task.FromResult(EditionsAnswer ?? new CatalogEditionsAnswer(
            CatalogAnswerOutcome.Unsupported,
            Work: null,
            WorkTitle: string.Empty,
            Editions: [],
            Qualifiers: [],
            Reasons: [CatalogAnswerCodes.OperationNotSupported],
            Usage: CatalogAnswerUsage.None,
            ProviderId: null));
    }
}

/// <summary>
/// Answers the chat is meant to route from, built directly rather than re-derived. The selection rules themselves are covered by
/// <c>CatalogAnswerPolicyTests</c>; a conversation test asserts the routing, the retained state and the rendering.
/// </summary>
internal static class TestAnswer
{
    public static readonly SourceRef Series = new("fake", "S-LANTERN", ExternalReferenceEntityType.Series);

    public static readonly SourceRef Author = new("fake", "A-QUILL", ExternalReferenceEntityType.Author);

    public static SourceRef Work(string title) =>
        new("fake", "w-" + title.Replace(' ', '-').ToLowerInvariant(), ExternalReferenceEntityType.Work);

    public static CatalogAnswerEntry Entry(
        string title, decimal? position = null, CatalogWorkClass workClass = CatalogWorkClass.Unknown, string? language = null)
    {
        System.ArgumentNullException.ThrowIfNull(title);
        return new CatalogAnswerEntry
        {
            Source = Work(title),
            Title = title,
            Credits = [new CatalogEntryCredit(0, "Mara Quill", null, Author, null)],
            Positions = position is { } sort
                ? [new PositionClaim(sort.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), sort, SeriesOrderingKind.Publication, Series)]
                : [],
            Class = workClass,
            Language = language,
            Publication = PublicationEvidence.Dated(new DateOnly(2021, 1, 1), DatePrecision.Year, DateBasis.Original),
            ProviderId = "fake",
            ObservedAtUtc = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero),
            SeriesLabel = "The Lantern Road"
        };
    }

    public static CatalogAnswer Members(params CatalogAnswerEntry[] entries) =>
        Build(CatalogAnswerOperation.SeriesMembers, CatalogAnswerOutcome.Qualified, entries, [CatalogAnswerCodes.ListNotExhaustive]);

    public static CatalogAnswer Position(decimal position, params CatalogAnswerEntry[] entries) =>
        Build(CatalogAnswerOperation.Position, CatalogAnswerOutcome.Qualified, entries, [CatalogAnswerCodes.TiedPosition], position);

    public static CatalogAnswer Latest(params CatalogAnswerEntry[] entries) =>
        Build(CatalogAnswerOperation.LatestPublished, CatalogAnswerOutcome.Qualified, entries, [CatalogAnswerCodes.IncompleteCoverage]);

    public static CatalogAnswer Nothing(CatalogAnswerOperation operation, string reason) =>
        Build(operation, CatalogAnswerOutcome.NoVerifiedMatch, [], [], null, [reason]);

    public static CatalogAnswer Offering(params CatalogAnswerChoice[] choices) =>
        new()
        {
            Operation = CatalogAnswerOperation.SeriesMembers,
            Outcome = CatalogAnswerOutcome.Clarification,
            Choices = choices,
            Reasons = [CatalogAnswerCodes.SeriesChoices],
            Today = new DateOnly(2026, 11, 18)
        };

    public static CatalogAnswerChoice SeriesChoice(string label = "The Lantern Road") =>
        new(CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, label, Series), "Mara Quill", 4, "fake", "series-claim-on-seed-record");

    public static CatalogAnswerChoice AuthorChoice(string label = "Mara Quill") =>
        new(CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, label, Author), "b. 1970", 12, "fake", "credit-on-seed-record");

    private static CatalogAnswer Build(
        CatalogAnswerOperation operation, CatalogAnswerOutcome outcome, CatalogAnswerEntry[] entries, string[] qualifiers, decimal? position = null,
        string[]? reasons = null) =>
        new()
        {
            Operation = operation,
            Outcome = outcome,
            Position = position,
            Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Lantern Road", Series),
            Entries = entries,
            Qualifiers = qualifiers,
            Reasons = reasons ?? [],
            Today = new DateOnly(2026, 11, 18)
        };
}

internal sealed class FakeAccounts(UserStatus status = UserStatus.Active) : IUserAccountStore
{
    public UserStatus Status { get; set; } = status;

    public bool Missing { get; set; }

    public Task<UserAccount?> FindAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<UserAccount?>(Missing
            ? null
            : new UserAccount(userId, "reader@example.test", "Test Reader", Status, false, DateTimeOffset.UnixEpoch, null));

    public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> CountAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountOperationResult> CreateAsync(string email, string displayName, string password, UserStatus status, bool isAdmin, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountOperationResult> SetStatusAsync(Guid userId, UserStatus status, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountOperationResult> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountOperationResult> SetAdminAsync(Guid userId, bool isAdmin, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountOperationResult> SetAudiobookNarrationPreferenceAsync(Guid userId, AudiobookNarrationPreference preference, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<AdminAccountSummary>> ListActiveAdminsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<QuietHours?> GetQuietHoursAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AccountOperationResult> SetQuietHoursAsync(Guid userId, QuietHours? quietHours, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class ConversationHarness
{
    public ConversationHarness(bool enabled = true, Guid? userId = null)
    {
        Options = new ConversationOptions { Enabled = enabled, MatrixEnabled = enabled, WebEnabled = enabled };
        User = new FakeCurrentUser(userId ?? Guid.NewGuid());
        Transaction = new FakeTransaction(Sessions, Events);
        Answers.Store = AnswerContexts;
        Answers.Now = () => Clock.UtcNow;
    }

    public ConversationOptions Options { get; }

    public FakeCurrentUser User { get; }

    public FakeClock Clock { get; } = new();

    public InMemorySessionStore Sessions { get; } = new();

    public InMemoryEventLog Events { get; } = new();

    public FakeTransaction Transaction { get; }

    public FakeLimiter Limiter { get; } = new();

    public FakeTools Tools { get; } = new();

    public FakeAnswers Answers { get; } = new();

    public CatalogAnswerContextStore AnswerContexts { get; set; } = new();

    /// <summary>When set, the engine asks this service instead of the scripted one (for tests that run the real shared answer service).</summary>
    public ICatalogAnswerService? AnswerService { get; set; }

    /// <summary>The account the async-boundary revalidation reads. Set it to a disabled status to withdraw the principal mid-test.</summary>
    public FakeAccounts Accounts { get; } = new();

    public IBookIntentInterpreter? Interpreter { get; set; }

    public IConversationDiagnostics? Diagnostics { get; set; }

    public ConversationService Service => new(
        User, Tools, Sessions, Events, Transaction, Limiter, Options, Clock, AnswerService ?? Answers, AnswerContexts, Interpreter, Diagnostics, accounts: Accounts);

    public int EventCounter { get; private set; }

    /// <summary>The transport and scope later messages default to, so one test can keep talking in the same conversation.</summary>
    public string DefaultTransport { get; set; } = ConversationTransports.Matrix;

    public string DefaultScope { get; set; } = "!room:example.test";

    public string NextEventId() => $"$event{++EventCounter}";

    public Task<ConversationTurnResult> SayAsync(string text, string? eventId = null, string? transport = null, string? scope = null) =>
        Service.HandleAsync(new ConversationTurn(transport ?? DefaultTransport, scope ?? DefaultScope, eventId ?? NextEventId(), text), CancellationToken.None);

    /// <summary>Registers three fictional results for a query.</summary>
    public ConversationHarness WithResults(string query)
    {
        Tools.Results[query] = new ConversationSearchResult(
            [FakeTools.Choice("a", "The Lantern Road"), FakeTools.Choice("b", "The Lantern Road: Companion Guide", "Other Author"), FakeTools.Choice("c", "Lantern Road Stories")],
            false, []);
        return this;
    }
}
