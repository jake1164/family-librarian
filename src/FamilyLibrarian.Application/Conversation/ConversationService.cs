using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Accounts;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// The one conversation engine behind every transport (alpha.6 M2). A Matrix message and a web message take the same path:
/// the caller is the authenticated principal of the current scope, the text is read by fixed rules, and the only actions
/// available are the book tools. No model is involved.
/// </summary>
public interface IConversationService
{
    Task<ConversationTurnResult> HandleAsync(ConversationTurn turn, CancellationToken cancellationToken);

    /// <summary>Records that a turn's reply reached the user, so a later replay does not send it again.</summary>
    Task MarkDeliveredAsync(ConversationEventKey key, CancellationToken cancellationToken);
}

public sealed partial class ConversationService(
    ICurrentUser currentUser,
    IConversationTools tools,
    IConversationSessionStore sessions,
    IConversationEventLog events,
    IConversationTransaction transaction,
    IConversationLimiter limiter,
    ConversationOptions options,
    IClock clock,
    ICatalogAnswerService answers,
    CatalogAnswerContextStore answerContexts,
    IBookIntentInterpreter? interpreter = null,
    IConversationDiagnostics? diagnostics = null,
    IModelWorkflowObserver? workflow = null,
    IUserAccountStore? accounts = null) : IConversationService
{
    private const int MaxScopeKeyLength = 200;
    private const int MaxEventIdLength = 255;
    private const int MaxReplyJsonLength = 8_000;
    private const string UnpublishedNotice = "I can't see books that haven't been published yet";

    public async Task<ConversationTurnResult> HandleAsync(ConversationTurn turn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (!ConversationTransports.IsKnown(turn.Transport) || !options.IsEnabledFor(turn.Transport))
        {
            return Terminal(ConversationReplyKind.Disabled, "Chat isn't turned on for this account's installation.");
        }

        // The principal is re-read on every turn. Nothing about the user is cached between messages.
        if (currentUser.UserId is not { } userId)
        {
            return Terminal(ConversationReplyKind.Unauthenticated, "You need to be signed in to Family Librarian to use chat.");
        }

        var scopeKey = turn.ScopeKey?.Trim() ?? string.Empty;
        var eventId = string.IsNullOrWhiteSpace(turn.EventId) ? null : turn.EventId.Trim();
        if (scopeKey.Length is 0 or > MaxScopeKeyLength || eventId is { Length: > MaxEventIdLength })
        {
            return Terminal(ConversationReplyKind.Unavailable, "That message couldn't be processed.");
        }

        var text = turn.Text ?? string.Empty;
        if (text.Length > options.MaxInputCharacters)
        {
            return Terminal(ConversationReplyKind.TooLong, $"Please keep messages under {options.MaxInputCharacters:N0} characters.");
        }

        // Read the message first (pure, bounded by the length check above) so control messages can skip the rate allowance.
        var parsed = ConversationParser.Parse(text);
        using var lease = await limiter.TryEnterAsync(userId, cancellationToken, countsTowardLimit: CountsAsTurn(parsed));
        if (lease is null)
        {
            return Terminal(ConversationReplyKind.RateLimited, "You're sending messages quickly. Give me a moment, then try again.");
        }

        try
        {
            if (eventId is not null && await events.FindAsync(turn.Transport, scopeKey, eventId, cancellationToken) is { } seen)
            {
                return Replay(seen, userId, turn.Transport, scopeKey, eventId);
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var processed = await ProcessAsync(userId, turn.Transport, scopeKey, eventId, parsed, turn.ListId, cancellationToken);
            diagnostics?.StepCompleted(ConversationSteps.Turn, System.Diagnostics.Stopwatch.GetElapsedTime(started), processed.Reply.Kind.ToString());
            return processed;
        }
        catch (ConversationUnauthenticatedException)
        {
            return Terminal(ConversationReplyKind.Unauthenticated, "You need to be signed in to Family Librarian to use chat.");
        }
    }

    public Task MarkDeliveredAsync(ConversationEventKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return events.MarkDeliveredAsync(key.Transport, key.ScopeKey, key.EventId, cancellationToken);
    }

    /// <summary>
    /// Whether a message is real work: it counts toward the session's turn limit and the per-user rate limit. Cancel, help, status
    /// and empty messages are control messages that touch no catalog or model, so neither limit applies to them.
    /// </summary>
    private static bool CountsAsTurn(ParsedConversationMessage parsed) =>
        parsed.Kind is not (ConversationCommandKind.Help or ConversationCommandKind.Cancel or ConversationCommandKind.Status
            or ConversationCommandKind.Empty);

    /// <summary>The messages that act on the list the reader is looking at. A button for an earlier list must not reach any of them.</summary>
    private static bool ActsOnList(ParsedConversationMessage parsed) =>
        parsed.Kind is ConversationCommandKind.Pick or ConversationCommandKind.Details or ConversationCommandKind.Request
            or ConversationCommandKind.Format or ConversationCommandKind.Reference or ConversationCommandKind.Editions
            or ConversationCommandKind.More or ConversationCommandKind.Cancel;

    private async Task<ConversationTurnResult> ProcessAsync(
        Guid userId, string transport, string scopeKey, string? eventId, ParsedConversationMessage parsed, string? listId,
        CancellationToken cancellationToken)
    {
        // The account is read again for every message that does work, so a person removed since their last message gets nothing:
        // not a lookup, not a pick, and never a request. The checks that follow guard the awaits inside the turn.
        if (await PrincipalWithdrawnAsync(cancellationToken))
        {
            return Terminal(ConversationReplyKind.Unauthenticated, "Your Family Librarian account can no longer use chat.");
        }

        var now = clock.UtcNow;
        var session = await sessions.FindAsync(userId, transport, scopeKey, cancellationToken)
            ?? new ConversationSession(userId, transport, scopeKey, ConversationState.Empty(), 0, now, now);
        var expired = false;
        if (now - session.LastActivityAtUtc > TimeSpan.FromMinutes(options.IdleExpiryMinutes))
        {
            expired = session.State.Phase != ConversationPhase.Idle;
            session.State = ConversationState.Empty();
            session.TurnCount = 0;
            session.StartedAtUtc = now;
        }

        var countsAsTurn = CountsAsTurn(parsed);
        TurnPlan plan;
        if (countsAsTurn && session.TurnCount >= options.MaxTurnsPerSession)
        {
            plan = new TurnPlan(
                ConversationReply.Of(ConversationReplyKind.Throttled,
                    "We've covered a lot, so I've cleared this conversation. Send your search again to start fresh."),
                ConversationState.Empty(), null, ResetTurns: true);
        }
        else if (listId is not null && ActsOnList(parsed) && !string.Equals(listId, session.State.ListId, StringComparison.Ordinal))
        {
            // A button drawn for an earlier list: the reader has since been shown another, so this press addresses nothing.
            plan = Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "That button belongs to an earlier list, so I haven't done anything. Use the newest answer, or ask again."), session.State);
        }
        else if (expired && parsed.Kind is ConversationCommandKind.Pick or ConversationCommandKind.Request or ConversationCommandKind.Format
                     or ConversationCommandKind.Details or ConversationCommandKind.Reference or ConversationCommandKind.More
                     or ConversationCommandKind.Editions)
        {
            plan = Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"That search expired after {options.IdleExpiryMinutes} minutes without activity. Tell me the book again and I'll look it up."),
                ConversationState.Empty());
        }
        else
        {
            plan = await PlanAsync(parsed, session.State, transport, scopeKey, cancellationToken);
        }

        plan = Stamp(plan);

        var reply = await transaction.ExecuteAsync(async token =>
        {
            if (eventId is not null && !await events.TryClaimAsync(userId, transport, scopeKey, eventId, token))
            {
                return null;
            }

            var finalReply = plan.Reply;
            var finalState = plan.State;
            if (plan.Request is { } pending)
            {
                finalReply = await CreateRequestAsync(userId, transport, scopeKey, eventId, pending, token);
                finalState = ConversationState.Empty();
            }

            session.State = finalState;
            session.TurnCount = plan.ResetTurns ? 0 : session.TurnCount + (countsAsTurn ? 1 : 0);
            session.LastActivityAtUtc = now;
            await sessions.SaveAsync(session, token);
            if (eventId is not null)
            {
                await events.CompleteAsync(transport, scopeKey, eventId, SerializeReply(finalReply), token);
            }

            return finalReply;
        }, cancellationToken);

        if (reply is null)
        {
            // Lost a race with another delivery of the same message: that one's recorded answer is the answer.
            var recorded = await events.FindAsync(transport, scopeKey, eventId!, cancellationToken);
            return recorded is null
                ? Terminal(ConversationReplyKind.Unavailable, "That message is already being handled.")
                : Replay(recorded, userId, transport, scopeKey, eventId!);
        }

        return new ConversationTurnResult(
            reply, Replayed: false, DeliveryPending: true,
            eventId is null ? null : new ConversationEventKey(transport, scopeKey, eventId));
    }

    private async Task<ConversationReply> CreateRequestAsync(
        Guid userId, string transport, string scopeKey, string? eventId, PendingRequest pending, CancellationToken cancellationToken)
    {
        // A look before the claim, so a refused turn leaves no key behind...
        if (await RefusalBeforeWriteAsync(pending, transport, scopeKey, cancellationToken) is { } early) return early;

        string? requestKey = eventId is null ? null : RequestKey(transport, scopeKey, eventId);
        if (requestKey is not null)
        {
            var (claimed, existingId) = await events.TryClaimRequestKeyAsync(userId, requestKey, cancellationToken);
            if (!claimed)
            {
                return new ConversationReply(
                    ConversationReplyKind.RequestExists,
                    $"“{pending.Title}” was already requested from that message.", [], [], ["status"], existingId);
            }

            // ...and the last look after it, because the claim was awaited: the person may have been withdrawn, or the answer they
            // picked from may have expired, in that time. Neither reaches the request tools, and the key is released so the recorded
            // refusal and the key agree (the same message does not later read as "already requested" with no request behind it).
            if (await RefusalBeforeWriteAsync(pending, transport, scopeKey, cancellationToken) is { } late)
            {
                await events.ReleaseRequestKeyAsync(requestKey, cancellationToken);
                return late;
            }
        }
        var result = await tools.CreateBookRequestAsync(pending.WorkId, pending.Formats, cancellationToken);
        switch (result.Outcome)
        {
            case CreateBookRequestOutcome.Created:
                if (requestKey is not null)
                {
                    await events.AttachRequestAsync(requestKey, result.Request!.Id, cancellationToken);
                }

                return new ConversationReply(
                    ConversationReplyKind.RequestCreated,
                    $"Requested “{pending.Title}” as {FormatList(pending.Formats)}. It goes through the usual Family Librarian process; " +
                    "say “status” to see how it's going.",
                    [], [], ["status"], result.Request!.Id);
            case CreateBookRequestOutcome.DuplicateWarning:
                return ConversationReply.Of(ConversationReplyKind.RequestExists,
                    $"You already have an outstanding request for “{pending.Title}”" +
                    (result.Request is null ? "." : $" ({Humanize(result.Request.Status.ToString())}).") +
                    " I haven't made another. Open Family Librarian if you need a different version.", "status");
            case CreateBookRequestOutcome.OwnedWarning:
                return ConversationReply.Of(ConversationReplyKind.RequestExists,
                    $"“{pending.Title}” is already in the library ({string.Join(", ", result.OwnedFormats.Select(format => format.MediaType.ToString().ToLowerInvariant()))}). " +
                    "Open Family Librarian if you still want another copy.");
            case CreateBookRequestOutcome.Unauthenticated:
                return ConversationReply.Of(ConversationReplyKind.Unauthenticated, "You need to be signed in to Family Librarian to request books.");
            case CreateBookRequestOutcome.WorkNotFound:
                return ConversationReply.Of(ConversationReplyKind.Unavailable, "I couldn't find that book in the catalog any more. Try searching again.");
            default:
                return ConversationReply.Of(ConversationReplyKind.Unavailable, result.Error ?? "That request couldn't be created right now.");
        }
    }

    /// <summary>
    /// The refusal, if any, to make just before a request is written: the account is read afresh and the answer's retained root is
    /// still held for this reader. Null means both still hold.
    /// </summary>
    private async Task<ConversationReply?> RefusalBeforeWriteAsync(PendingRequest pending, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (await PrincipalWithdrawnAsync(cancellationToken))
        {
            return ConversationReply.Of(ConversationReplyKind.Unauthenticated, "Your Family Librarian account can no longer use chat.");
        }

        return pending.AnswerRoot is { } root && !AnswerRootLive(transport, scopeKey, root)
            ? ExpiredChoicePlan(ConversationState.Empty()).Reply
            : null;
    }
    private async Task<TurnPlan> PlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        switch (parsed.Kind)
        {
            case ConversationCommandKind.Help:
            case ConversationCommandKind.Empty:
                return Keep(Help(), state);

            case ConversationCommandKind.Cancel:
                return new TurnPlan(ConversationReply.Of(ConversationReplyKind.Cancelled, "Okay, cleared. Tell me a book to look for whenever you're ready."),
                    ConversationState.Empty(), null, ResetTurns: true);

            case ConversationCommandKind.OutOfDomain:
                return Keep(ConversationReply.Of(ConversationReplyKind.OutOfDomain,
                    "I can only help with books: finding them, checking the library, and your book requests. " +
                    "Try “find The Hobbit”, “status”, or “help”."), state);

            case ConversationCommandKind.Status:
                return Keep(await StatusReplyAsync(cancellationToken), state);

            case ConversationCommandKind.Library:
                return Keep(await LibraryReplyAsync(parsed.Text!, cancellationToken), state);

            case ConversationCommandKind.Series:
                return await SeriesPlanAsync(parsed, state, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Isbn:
                return await SearchPlanAsync(parsed.Text!, $"ISBN {parsed.Text}", null, requestIntent: false, state, cancellationToken);

            case ConversationCommandKind.Search:
                return await SearchPlanAsync(
                    parsed.Text!, parsed.Text!, parsed.Formats, parsed.RequestIntent, state, cancellationToken, tryInterpretIfEmpty: true);

            case ConversationCommandKind.Details:
                return Keep(await DetailsReplyAsync(state, parsed.Number!.Value, transport, scopeKey, cancellationToken), state);

            case ConversationCommandKind.More:
                return await MorePlanAsync(state, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Reference:
                return await ReferencePlanAsync(parsed, state, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Release:
                return await ReleasePlanAsync(parsed, state, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Author:
                return await AuthorPlanAsync(parsed, state, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Editions:
                return await EditionsPlanAsync(parsed, state, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Pick:
                if (state.Phase == ConversationPhase.ChoosingSubject)
                {
                    return await SubjectPickPlanAsync(state, parsed.Number!.Value, transport, scopeKey, cancellationToken);
                }

                if (state.Phase == ConversationPhase.ChoosingOrder)
                {
                    return await OrderPickPlanAsync(state, parsed.Number!.Value, transport, scopeKey, cancellationToken);
                }

                return await PickPlanAsync(state, parsed.Number!.Value, parsed.Formats, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Request:
                if (state.Phase is ConversationPhase.ChoosingSubject or ConversationPhase.ChoosingOrder)
                {
                    return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                        "Pick the one you mean by number first, then I can request it."), state);
                }

                return await PickPlanAsync(state, parsed.Number!.Value, parsed.Formats, transport, scopeKey, cancellationToken);

            case ConversationCommandKind.Format:
                return await FormatPlanAsync(state, parsed.Formats!, transport, scopeKey, cancellationToken);

            default:
                return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood, "I didn't catch that. Say “help” to see what I can do."), state);
        }
    }

    // ======================================================================================== shared catalog answers

    /// <summary>
    /// "Latest", "book N", "in order": one question put to the shared answer service. The wording decides which operation is asked
    /// and whether a filter was stated; the answer decides what is shown. A question that names a series or author is answered with
    /// choices first, never with a subject guessed from the name.
    /// </summary>
    private async Task<TurnPlan> SeriesPlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (parsed.SeriesAfterTitle is not null)
        {
            // "what comes after <a title>": without the book's own identity there is no anchor to continue from, so the labelled
            // catalog fallback is used and never presented as a settled next-in-order fact.
            return await AfterTitlePlanAsync(parsed, state, transport, scopeKey, cancellationToken);
        }

        var operation = parsed.SeriesPosition switch
        {
            null => CatalogAnswerOperation.SeriesMembers,
            SeriesPositionResolver.LastPosition => CatalogAnswerOperation.LatestPublished,
            _ => CatalogAnswerOperation.Position
        };
        var filter = FilterOf(parsed.Text);
        var request = new CatalogAnswerRequest
        {
            Operation = operation,
            Text = string.IsNullOrWhiteSpace(parsed.SeriesName) ? null : parsed.SeriesName,
            Position = operation == CatalogAnswerOperation.Position ? parsed.SeriesPosition : null,
            Filter = filter
        };
        var (plan, answer) = await AskAsync(request, filter, transport, scopeKey, state, parsed.RequestIntent, parsed.Formats, cancellationToken);

        // The name matched no series at all (not "the position is missing"): the ordinary search still helps, and the reply says the
        // order could not be confirmed rather than presenting keyword hits as an answer. A title that merely looks like a series
        // question is still shown as typed when the catalog has exactly that title.
        if (answer is { Entries.Count: 0, Choices.Count: 0, AvailableOrders.Count: 0 } &&
            answer.Reasons.Contains(CatalogAnswerCodes.NoChoicesFound, StringComparer.Ordinal))
        {
            return await SeriesNotFoundPlanAsync(parsed, state, cancellationToken);
        }

        return plan;
    }

    /// <summary>
    /// Unreleased books, release dates and release years are answered from the shared answer service where a source states them, and
    /// said plainly where nothing does. A named title is still looked up so the person gets something useful.
    /// </summary>
    private async Task<TurnPlan> ReleasePlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (parsed.Action == ConversationActions.Year)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                "I can't filter by release year. Ask about a series or a title and I'll look up what the sources state."), state);
        }

        var operation = parsed.Action switch
        {
            ConversationActions.When => CatalogAnswerOperation.PublicationForTitle,
            ConversationActions.Next => CatalogAnswerOperation.NextRelease,
            _ => CatalogAnswerOperation.UpcomingReleases
        };
        var subject = parsed.Action == ConversationActions.When
            ? parsed.Text
            : string.IsNullOrWhiteSpace(parsed.SeriesName) ? parsed.Text : parsed.SeriesName;
        var request = new CatalogAnswerRequest { Operation = operation, Text = subject };
        var (plan, _) = await AskAsync(request, CatalogAnswerFilter.None, transport, scopeKey, state, requestIntent: false, formats: null, cancellationToken);
        return plan;
    }

    /// <summary>"What has X written", "what series is X credited on": an author question, answered with choices first.</summary>
    private async Task<TurnPlan> AuthorPlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        var operation = parsed.Action == ConversationActions.Series
            ? CatalogAnswerOperation.AuthorSeries
            : CatalogAnswerOperation.AuthorWorks;
        var request = new CatalogAnswerRequest { Operation = operation, Text = parsed.Text };
        var (plan, _) = await AskAsync(request, CatalogAnswerFilter.None, transport, scopeKey, state, requestIntent: false, formats: null, cancellationToken);
        if (plan.Reply.Kind is ConversationReplyKind.Clarification or ConversationReplyKind.Answer) return plan;

        // Nothing that could be an author was found: the wording may still be a title ("Books by the Sea"), so it is searched as
        // typed rather than refused.
        return await SearchPlanAsync(parsed.Text!, parsed.Text!, null, requestIntent: false, state, cancellationToken);
    }

    /// <summary>
    /// "editions": the source's edition titles for the record the conversation is about. The record is resolved from the server's own
    /// retained answer, so an expired or foreign list means no provider call at all.
    /// </summary>
    private async Task<TurnPlan> EditionsPlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (RootOf(state) is not { } root || CallerOf(transport, scopeKey) is not { } caller)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "I don't have a book to list edition titles for. Tell me a book first, or ask about a series again."), state);
        }

        var number = parsed.Number ?? (state.EntryChoices.Count == 1 ? 1 : 0);
        if (number == 0)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"Which book? Say “editions 1” to “editions {state.EntryChoices.Count}”."), state);
        }

        if (number < 1 || number > state.EntryChoices.Count || state.EntryChoices[number - 1]?.ToReference() is not { } selected)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"Pick a book from 1 to {state.EntryChoices.Count} of the list I showed."), state);
        }

        if (await PrincipalWithdrawnAsync(cancellationToken)) return WithdrawnPlan();

        var editions = await answers.EditionsAsync(caller, root, selected, cancellationToken);
        var rendered = ConversationAnswerText.ForEditions(editions, BudgetFor(transport));
        var kind = editions.Editions.Count > 0 ? ConversationReplyKind.Editions : ConversationReplyKind.Answer;
        return Keep(new ConversationReply(kind, rendered.Text, [], [], ["cancel"], null, rendered.Cards), state);
    }

    /// <summary>One question to the shared answer service, with the principal re-read before any provider work.</summary>
    private async Task<(TurnPlan Plan, CatalogAnswer? Answer)> AskAsync(
        CatalogAnswerRequest request, CatalogAnswerFilter filter, string transport, string scopeKey, ConversationState state,
        bool requestIntent, IReadOnlyList<RequestMediaType>? formats, CancellationToken cancellationToken, IReadOnlySet<string>? alreadyShown = null)
    {
        if (CallerOf(transport, scopeKey) is not { } caller)
        {
            return (Keep(ConversationReply.Of(ConversationReplyKind.Unauthenticated,
                "You need to be signed in to Family Librarian to ask about books."), state), null);
        }

        if (await PrincipalWithdrawnAsync(cancellationToken)) return (WithdrawnPlan(), null);

        var answer = await answers.AnswerAsync(caller, request, cancellationToken);
        return (await AnswerTurnPlanAsync(answer, filter, requestIntent, formats, state, transport, scopeKey, cancellationToken, alreadyShown), answer);
    }

    /// <summary>
    /// The answer the reader is shown and the state the next turn acts on, built together so they are one list. The numbers drawn are
    /// the most that the bounded state keeps whole, then the fewest that the reply's budget lets the text show; the state keeps
    /// exactly those, so a number on the screen is never one the conversation refuses.
    /// </summary>
    private async Task<TurnPlan> AnswerTurnPlanAsync(
        CatalogAnswer answer, CatalogAnswerFilter filter, bool requestIntent, IReadOnlyList<RequestMediaType>? formats,
        ConversationState previous, string transport, string scopeKey, CancellationToken cancellationToken, IReadOnlySet<string>? alreadyShown = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var budget = BudgetFor(transport);
        var context = answer.Context?.ToJson();
        var next = new ConversationState
        {
            Query = previous.Query,
            FormatHint = formats?.ToList() ?? [],
            RequestIntent = requestIntent,
            AnswerOperation = answer.Operation,
            AnswerPosition = answer.Position,
            AnswerNovelOnly = filter.NovelOnly,
            AnswerMainOnly = filter.MainOnly
        };

        // The largest count of numbered items whose state is kept whole. Fewer than that may be shown (a long line can use the budget),
        // never more.
        int Fit(int available, Action<int> build)
        {
            for (var count = available; count > 0; count--)
            {
                build(count);
                if (next.FitsWhole()) return count;
            }

            build(0);
            return 0;
        }

        ConversationAnswerText.Rendered rendered;
        if (answer.Outcome == CatalogAnswerOutcome.ContextExpired)
        {
            // Nothing is refetched and nothing is selected: the reader restarts, as the retained context layer requires.
            next = new ConversationState { FormatHint = formats?.ToList() ?? [], RequestIntent = requestIntent };
            rendered = ConversationAnswerText.ForAnswer(answer, budget, 0);
        }
        else if (answer.Outcome == CatalogAnswerOutcome.Clarification && answer.Choices.Count > 0)
        {
            var offerable = ConversationAnswerText.OfferableChoices(answer);
            void Build(int count)
            {
                next.SubjectChoices = offerable.Take(count).Select(ConversationSubjectChoice.From).OfType<ConversationSubjectChoice>().ToList();
                next.Phase = next.SubjectChoices.Count > 0 ? ConversationPhase.ChoosingSubject : ConversationPhase.Idle;
                next.AnswerContext = context;
            }

            var fit = Fit(offerable.Count, Build);
            rendered = ConversationAnswerText.ForAnswer(answer, budget, fit);
            Build(rendered.Offered);
        }
        else if (answer.Outcome == CatalogAnswerOutcome.Clarification && answer.AvailableOrders.Count > 0)
        {
            next.Phase = ConversationPhase.ChoosingOrder;
            next.Orders = answer.AvailableOrders.Take(ConversationState.MaxSubjectChoices).ToList();
            next.SubjectChoices = answer.Subject is { } selected && ConversationSubjectChoice.From(
                new CatalogAnswerChoice(selected, null, null, null, null)) is { } kept
                ? [kept]
                : [];
            next.AnswerContext = context;
            rendered = ConversationAnswerText.ForAnswer(answer, budget, 0);
        }
        else if (answer.Series.Count > 0)
        {
            // An author's series: each one with an identity can be opened by number; the rest are listed and say why they cannot be.
            var offerable = ConversationAnswerText.OfferableSeries(answer, alreadyShown);
            void Build(int count)
            {
                next.SubjectChoices = offerable.Take(count)
                    .Select(ConversationAnswerText.SubjectChoiceOf)
                    .OfType<ConversationSubjectChoice>()
                    .ToList();
                next.Phase = next.SubjectChoices.Count > 0 ? ConversationPhase.ChoosingSubject : ConversationPhase.Idle;
                next.PicksFromSeries = next.SubjectChoices.Count > 0;
                next.AnswerContext = context;
            }

            var fit = Fit(offerable.Count, Build);
            rendered = ConversationAnswerText.ForAnswer(answer, budget, fit, alreadyShown);
            Build(rendered.Offered);
        }
        else
        {
            var offerable = ConversationAnswerText.OfferableEntries(answer, alreadyShown);
            void Build(int count)
            {
                var taken = offerable.Take(count).ToArray();
                next.Choices = Pickable(taken).ToList();
                next.Phase = taken.Length > 0 ? ConversationPhase.ChoosingBook : ConversationPhase.Idle;
                next.AnswerContext = context;
                next.EntryChoices = taken.Select(FocusOf).ToList();
                next.FocusEntry = answer.Entries.Count == 1 && taken.Length == 1 ? next.EntryChoices[0] : null;
            }

            var fit = Fit(Math.Min(options.MaxChoices, offerable.Count), Build);
            rendered = ConversationAnswerText.ForAnswer(answer, budget, fit, alreadyShown);
            Build(rendered.Offered);
        }

        // Where "more" resumes: it shows the records of this answer not yet shown, first from what is retained and then from the
        // source's next page. What was shown is remembered by identity with the retained root, so a page that sorts new records in
        // among the earlier ones neither skips a new record nor repeats an old one.
        if (ConversationAnswerText.IsPageable(answer.Operation) && answer.Choices.Count == 0)
        {
            next.MoreEntries = rendered.Remaining;
            next.ContinuationToken = answer.ContinuationToken is { Length: > 0 and <= MaxContinuationTokenLength } token ? token : null;
            if (answer.Context is { } held && CallerOf(transport, scopeKey) is { } owner)
            {
                answerContexts.RecordShown(owner, held.RootId, rendered.ShownKeys ?? [], append: alreadyShown is not null, clock.UtcNow);
            }
        }
        var kind = answer.Outcome == CatalogAnswerOutcome.Clarification ? ConversationReplyKind.Clarification : ConversationReplyKind.Answer;
        var suggested = answer.Outcome == CatalogAnswerOutcome.Clarification && answer.Choices.Count > 0 ? new[] { "1", "cancel" } : [];
        var reply = new ConversationReply(kind, rendered.Text, rendered.Choices, [], suggested, null, rendered.Cards);

        // The person already said it was a request ("request the latest ..."): the existing format and confirmation flow continues.
        if (requestIntent && next.Choices.Count == 1 && kind == ConversationReplyKind.Answer)
        {
            return await PickPlanAsync(next, 1, formats, transport, scopeKey, cancellationToken);
        }

        return new TurnPlan(reply, next, null);
    }

    private const int MaxContinuationTokenLength = 128;

    /// <summary>
    /// "more": the next page of the answer being read. Records the retained answer still holds are shown first, with no provider
    /// read; only when they are all shown does one explicit "more" spend one provider page. Nothing is continued once the answer's
    /// context has expired or belongs to another reader, and an exhausted list says so without asking anything.
    /// </summary>
    private async Task<TurnPlan> MorePlanAsync(ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (state.AnswerContext is null || state.AnswerOperation is not { } operation || !ConversationAnswerText.IsPageable(operation)
            || !CatalogAnswerContextRef.TryParse(state.AnswerContext, out var reference) || reference is null)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "I don't have a list to show more of. Ask about a series or an author first."), state);
        }

        if (!ListLive(state, transport, scopeKey)) return ExpiredChoicePlan(state);
        if (state.MoreEntries <= 0 && state.ContinuationToken is null)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.Text, "There is nothing more to show for that list."), state);
        }

        var filter = FilterOf(state);
        var request = new CatalogAnswerRequest
        {
            Operation = operation,
            Position = state.AnswerPosition,
            Subject = reference.Subject,
            ContextRootId = reference.RootId,
            Order = reference.Order,
            Filter = filter,
            ContinuationToken = state.MoreEntries > 0 ? null : state.ContinuationToken
        };
        var shown = CallerOf(transport, scopeKey) is { } caller
            ? answerContexts.GetShown(caller, reference.RootId, clock.UtcNow)
            : new HashSet<string>(StringComparer.Ordinal);
        var (plan, answer) = await AskAsync(request, filter, transport, scopeKey, state, requestIntent: false, state.FormatHint, cancellationToken, shown);
        if (answer is null || answer.Outcome == CatalogAnswerOutcome.ContextExpired) return plan;

        if (ConversationAnswerText.PrimaryKeys(answer).Any(key => !shown.Contains(key))) return plan;

        // The read gave nothing further. The list the reader is looking at stays usable; the reply says why, and a continuation the
        // service refused is forgotten so the same word does not keep asking for it.
        var unavailable = answer.Reasons.Contains(CatalogAnswerCodes.ContinuationUnavailable, StringComparer.Ordinal);

        // A page that added no record the reader has not seen (a duplicate-only or empty page) can still have issued the cursor for
        // the page after it. That new cursor replaces the one just spent, so the next explicit "more" reads that page; nothing is
        // read on its own, and the displayed list and its choices are exactly as they were.
        if (!unavailable && answer.ContinuationToken is { Length: > 0 and <= MaxContinuationTokenLength } issued
            && !string.Equals(issued, state.ContinuationToken, StringComparison.Ordinal))
        {
            var advanced = state.WithoutMore();
            advanced.ContinuationToken = issued;
            return new TurnPlan(
                ConversationReply.Of(ConversationReplyKind.Text,
                    "That page had nothing new in it. Say “more” and I'll read the next one.", "more"),
                advanced, null);
        }

        var kept = unavailable || answer.Outcome is CatalogAnswerOutcome.Answered or CatalogAnswerOutcome.Qualified or CatalogAnswerOutcome.NoVerifiedMatch
            ? state.WithoutMore()
            : state;
        return new TurnPlan(
            plan.Reply with { Choices = [], Answers = null, SuggestedReplies = [] }, kept, null);
    }

    /// <summary>A retained subject choice is selected by number, but the reference itself is the one the server retained.</summary>
    private async Task<TurnPlan> SubjectPickPlanAsync(
        ConversationState state, int number, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (number < 1 || number > state.SubjectChoices.Count)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"Pick a number from 1 to {state.SubjectChoices.Count}, or say “cancel”."), state);
        }

        if (RootOf(state) is not { } root || CallerOf(transport, scopeKey) is not { } caller)
        {
            return ExpiredChoicePlan(state);
        }

        // The number on the screen is only a position, and the retained window is not necessarily in the order that was shown (the
        // answer sorts what the source returned). What the reader chose is the identity the screen offered, and that identity is
        // looked up in this owner's own retained window: the reference acted on is the retained one, or nothing is selected and no
        // provider is contacted.
        var filter = FilterOf(state);
        if (state.SubjectChoices[number - 1].ToReference() is not { } offered) return ExpiredChoicePlan(state);
        CatalogAnswerRequest request;
        if (state.PicksFromSeries)
        {
            // An author's series answer: the offered series, and its books are asked in the same root.
            var series = answerContexts.TryFindSeries(caller, root, offered.SavedId, offered.Native, clock.UtcNow);
            if (series is null || ConversationAnswerText.SubjectChoiceOf(series)?.ToReference() is not { } chosen)
            {
                return ExpiredChoicePlan(state);
            }

            request = new CatalogAnswerRequest
            {
                Operation = CatalogAnswerOperation.SeriesMembers,
                Subject = chosen,
                ContextRootId = root,
                Filter = filter
            };
        }
        else
        {
            var retained = answerContexts.TryFindChoice(caller, root, offered.Kind, offered.SavedId, offered.Native, offered.Order, clock.UtcNow);
            if (retained is null) return ExpiredChoicePlan(state);
            request = new CatalogAnswerRequest
            {
                Operation = state.AnswerOperation ?? CatalogAnswerOperation.SeriesMembers,
                Position = state.AnswerPosition,
                Subject = retained.Reference,
                ContextRootId = root,
                Order = retained.Reference.Order,
                Filter = filter
            };
        }
        var (plan, _) = await AskAsync(request, filter, transport, scopeKey, state, state.RequestIntent, state.FormatHint, cancellationToken);
        return plan;
    }

    /// <summary>The books state more than one ordering; the chosen one is applied to the same retained subject and read.</summary>
    private async Task<TurnPlan> OrderPickPlanAsync(
        ConversationState state, int number, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (number < 1 || number > state.Orders.Count)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"Pick a number from 1 to {state.Orders.Count}, or say “cancel”."), state);
        }

        if (RootOf(state) is not { } root || state.SubjectChoices.FirstOrDefault()?.ToReference() is not { } subject)
        {
            return ExpiredChoicePlan(state);
        }

        var filter = FilterOf(state);
        var request = new CatalogAnswerRequest
        {
            Operation = state.AnswerOperation ?? CatalogAnswerOperation.SeriesMembers,
            Position = state.AnswerPosition,
            Subject = subject.WithOrder(state.Orders[number - 1]),
            ContextRootId = root,
            Order = state.Orders[number - 1],
            Filter = filter
        };
        var (plan, _) = await AskAsync(request, filter, transport, scopeKey, state, state.RequestIntent, state.FormatHint, cancellationToken);
        return plan;
    }

    private static TurnPlan ExpiredChoicePlan(ConversationState state) =>
        new(
            ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "That list has expired, so I haven't looked anything up. Ask the question again and I'll start over."),
            new ConversationState { FormatHint = state.FormatHint }, null);

    private static CatalogAnswerFilter FilterOf(ConversationState state) => new(state.AnswerMainOnly, state.AnswerNovelOnly, null);

    /// <summary>"latest novel in ...", "main series": a stated filter is part of the question and continues on a follow-up.</summary>
    internal static CatalogAnswerFilter FilterOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return CatalogAnswerFilter.None;
        var lower = text.ToLowerInvariant();
        var novel = NovelCuePattern().IsMatch(lower);
        var main = MainCuePattern().IsMatch(lower);
        return novel || main ? new CatalogAnswerFilter(main, novel, null) : CatalogAnswerFilter.None;
    }

    private static Guid? RootOf(ConversationState state) =>
        state.AnswerContext is { } json && CatalogAnswerContextRef.TryParse(json, out var reference) && reference is not null
            ? reference.RootId
            : null;

    private CatalogAnswerCaller? CallerOf(string transport, string scopeKey) =>
        currentUser.UserId is { } userId && scopeKey.Length is > 0 and <= CatalogAnswerCaller.MaxScopeKeyLength
            ? new CatalogAnswerCaller(
                userId,
                transport == ConversationTransports.Matrix ? CatalogAnswerTransport.Matrix : CatalogAnswerTransport.Web,
                scopeKey)
            : null;

    /// <summary>
    /// The principal is re-read at the async boundaries that matter: before provider work and before an action. A web turn and a
    /// Matrix turn both take this path; the transports' own authentication is unchanged, and no identity ever comes from a body.
    /// </summary>
    private async Task<bool> PrincipalWithdrawnAsync(CancellationToken cancellationToken)
    {
        if (accounts is null || currentUser.UserId is not { } userId) return false;
        var account = await accounts.FindAsync(userId, cancellationToken);
        return account is null || !UserStatuses.CanSignIn(account.Status);
    }

    private static int BudgetFor(string transport) =>
        transport == ConversationTransports.Matrix ? ConversationAnswerText.MatrixMaxLength : ConversationAnswerText.WebMaxLength;

    /// <summary>
    /// The records as the state keeps them. The title is display text, so a very long one is cut with a visible mark and the
    /// record is still selected by its own id; the id is never cut.
    /// </summary>
    private static IEnumerable<ConversationChoiceRef> Pickable(IEnumerable<CatalogAnswerEntry> entries) =>
        entries.Select(entry => new ConversationChoiceRef(
            entry.Source?.ProviderId ?? string.Empty,
            entry.Source?.ExternalId ?? string.Empty,
            ConversationState.Ellipsize(entry.Title, ConversationState.MaxFieldLength, "Untitled record"),
            ConversationState.Clip(string.Join(", ", entry.Credits.Where(credit => credit.IsAuthorRole).Select(credit => credit.Label).Take(3))),
            ConversationState.Clip(EntryDetail(entry)),
            ConversationState.Clip(entry.SeriesLabel),
            entry.SavedWorkId));

    private static ConversationSubjectChoice? FocusOf(CatalogAnswerEntry entry)
    {
        try
        {
            var label = ConversationState.Ellipsize(entry.Title, ConversationSubjectChoice.MaxLabelLength, "Untitled record");
            var reference = entry.Source is { } source
                ? CatalogSubjectRef.FromSource(CatalogSubjectKind.Book, label, source)
                : entry.SavedWorkId is { } saved
                    ? CatalogSubjectRef.Saved(CatalogSubjectKind.Book, label, saved)
                    : null;
            return reference is null ? null : ConversationSubjectChoice.From(new CatalogAnswerChoice(reference, null, null, entry.ProviderId, null));
        }
        catch (ArgumentException)
        {
            // A reference that cannot be built whole is not offered for expansion: the record is shown, and the action says nothing.
            return null;
        }
    }

    private static string EntryDetail(CatalogAnswerEntry entry)
    {
        var parts = new List<string>();
        if (entry.Positions.FirstOrDefault(claim => claim.Sort is not null)?.Sort is { } sort)
        {
            parts.Add("book " + sort.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (entry.Publication.Date is { } date) parts.Add(date.Year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if (entry.Publication.State == PublicationDateState.KnownUndated) parts.Add("no date stated");
        if (!string.IsNullOrWhiteSpace(entry.Language)) parts.Add(entry.Language!.Trim());
        return string.Join(" · ", parts);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bnovels?\b")]
    private static partial System.Text.RegularExpressions.Regex NovelCuePattern();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bmain\b")]
    private static partial System.Text.RegularExpressions.Regex MainCuePattern();


    /// <summary>
    /// "what comes after <i>a title</i>": the anchor's own identity is not held, so this uses the labelled catalog fallback — its
    /// results are explicitly choices, never a settled next-in-order answer.
    /// </summary>
    private async Task<TurnPlan> AfterTitlePlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        var lookup = await tools.FindSeriesBooksAsync(parsed.SeriesName!, parsed.SeriesPosition, parsed.SeriesAfterTitle, options.MaxChoices, cancellationToken);
        switch (lookup.Kind)
        {
            case SeriesLookupKind.NotFound:
                return await SeriesNotFoundPlanAsync(parsed, state, cancellationToken);


            case SeriesLookupKind.AmbiguousSeries:
                if (string.IsNullOrWhiteSpace(parsed.SeriesName) && parsed.SeriesAfterTitle is { } anchor)
                {
                    // No series was named: the book itself is in several, so name them rather than quote an empty name.
                    return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                        $"“{anchor}” is in more than one series: {string.Join("; ", lookup.AmbiguousSeriesNames)}. " +
                        $"Tell me which, for example “what comes after {anchor} in {lookup.AmbiguousSeriesNames[0]}”."), state);
                }

                return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                    $"More than one series matches “{parsed.SeriesName}”: {string.Join("; ", lookup.AmbiguousSeriesNames)}. " +
                    "Say which one, for example “book 1 of " + lookup.AmbiguousSeriesNames[0] + "”."), state);

            case SeriesLookupKind.PositionMissing:
                if (parsed.SeriesAfterTitle is not null)
                {
                    return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                        $"I don't know of a book after “{parsed.SeriesAfterTitle}” in {lookup.SeriesName}. It may be the latest, or the catalog may not list a newer one yet."), state);
                }

                var known = lookup.KnownPositions.Count == 0
                    ? "I don't have any numbered books for it"
                    : $"I know of {string.Join(", ", lookup.KnownPositions.Select(FormatPosition))}";
                return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                    $"I found the series {lookup.SeriesName}, but not that book. {known}. " +
                    "The catalog may not list every book in a series, so it could still exist."), state);
        }

        // "Latest" needs enough of the series to see. With only a book or two known, the highest of them is not evidence of the
        // newest (it may be a companion or an early book), so say how little is visible and search the series name instead.
        var choices = lookup.Books
            .Where(book => book.ExternalId.Length is > 0 and <= ConversationState.MaxFieldLength)
            .Select(book => new ConversationChoiceRef(
                book.ProviderId, book.ExternalId, ConversationState.Clip(book.Title), ConversationState.Clip(book.Authors),
                ConversationState.Clip(book.Position is null ? book.SeriesName : $"{book.SeriesName} #{FormatPosition(book.Position.Value)}"),
                string.IsNullOrWhiteSpace(book.SeriesName) ? null : ConversationState.Clip(book.SeriesName)))
            .ToList();
        var label = $"{lookup.SeriesName} after {parsed.SeriesAfterTitle}";
        var next = new ConversationState
        {
            Phase = ConversationPhase.ChoosingBook,
            Query = ConversationState.Clip(label),
            Choices = choices,
            FormatHint = parsed.Formats?.ToList() ?? [],
            RequestIntent = parsed.RequestIntent
        };
        if (choices.Count == 1 && parsed.RequestIntent)
        {
            return await PickPlanAsync(next, 1, parsed.Formats, transport, scopeKey, cancellationToken);
        }

        var shown = choices.Select((choice, index) => new ConversationChoice(index + 1, choice.Title, choice.Authors, choice.Detail)).ToArray();
        // This comes from whatever books the catalog happened to show for the series, so it is never worded as a settled fact.
        var lines = new StringBuilder(shown.Length == 1
            ? $"After {parsed.SeriesAfterTitle}, the next book in {lookup.SeriesName} is:"
            : $"After {parsed.SeriesAfterTitle}, more than one book is listed next in {lookup.SeriesName}:");
        foreach (var choice in shown)
        {
            var authors = choice.Authors.Length > 0 ? $" — {choice.Authors}" : "";
            lines.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n{choice.Number}. {choice.Title}{authors}");
        }

        lines.Append("\nReply with a number to pick one, or “request 1 audiobook”.");
        return new TurnPlan(new ConversationReply(ConversationReplyKind.Choices, lines.ToString(), shown, [], ["1", "cancel"]), next, null);
    }

    /// <summary>
    /// The series lookup found no series of that name. A bare title that only looks like a series question ("The First Book of
    /// Samuel") is searched as typed. A real question ("What is the 3rd Murderbot book?") is not, because the whole sentence is a
    /// poor search: the series name, or the named book, is searched instead and the reply says the order could not be confirmed.
    /// </summary>
    private async Task<TurnPlan> SeriesNotFoundPlanAsync(ParsedConversationMessage parsed, ConversationState state, CancellationToken cancellationToken)
    {
        if (parsed is { Action: ConversationActions.Next, SeriesAfterTitle: { } focused })
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                $"I couldn't tell what comes after “{focused}”: the catalog doesn't show me this series' order. " +
                "It may be the latest, or a newer book may not be listed."), state);
        }

        if (parsed.SeriesAfterTitle is { Length: > 0 } after)
        {
            return await SearchPlanAsync(
                after, after, parsed.Formats, requestIntent: false, state, cancellationToken,
                lead: $"I couldn't work out what comes after “{after}” because the catalog doesn't show me that series' order. Here's what I found for “{after}”:");
        }

        var text = parsed.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parsed.SeriesName))
        {
            return await SearchPlanAsync(text, text, parsed.Formats, parsed.RequestIntent, state, cancellationToken);
        }

        // A message that does not read as a sentence may still be a real title that merely looks like a series question ("The First
        // Book of ..."): show it as typed only when the catalog has a book of exactly that title. Otherwise it is a question about a
        // series we do not know, and the series name is the better search.
        if (!ConversationProse.LooksLikeProse(text))
        {
            var asTyped = await tools.SearchBooksAsync(text, options.MaxChoices, cancellationToken);
            if (ConversationProse.HasExactTitle(asTyped.Choices, text))
            {
                return await SearchPlanAsync(text, text, parsed.Formats, parsed.RequestIntent, state, cancellationToken);
            }
        }

        var series = parsed.SeriesName;
        return await SearchPlanAsync(
            series, series, parsed.Formats, parsed.RequestIntent, state, cancellationToken,
            lead: $"I couldn't confirm which books are in “{series}” from the catalog, so I can't give their order. Here's what I found for “{series}”:");
    }

    /// <summary>
    /// "it", "that one": an action on the single book the conversation is about. The book is the one chosen for a format, or the
    /// only one offered. When several were offered "it" is ambiguous and the person is asked for a number: nothing is ever guessed.
    /// </summary>
    private async Task<TurnPlan> ReferencePlanAsync(
        ParsedConversationMessage parsed, ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (parsed.Action == ConversationActions.Next && state.AnswerContext is not null)
        {
            return await NextAfterFocusPlanAsync(state, transport, scopeKey, cancellationToken);
        }

        if (FocusIndex(state) is not { } index)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                state.Choices.Count > 1
                    ? "Which book do you mean? Reply with its number first."
                    : "Tell me a book to look for first."), state);
        }

        var choice = state.Choices[index];
        switch (parsed.Action)
        {
            case ConversationActions.Request:
                return await PickPlanAsync(state, index + 1, parsed.Formats, transport, scopeKey, cancellationToken);

            case ConversationActions.Details:
                return Keep(await DetailsReplyAsync(state, index + 1, transport, scopeKey, cancellationToken), state);

            case ConversationActions.Availability:
                if (!ListLive(state, transport, scopeKey)) return ExpiredChoicePlan(state);
                if (string.IsNullOrEmpty(choice.ExternalId))
                {
                    // A record the library already holds: its own page is the check, and nothing is looked up again.
                    return Keep(ConversationReply.Of(ConversationReplyKind.Library,
                        $"“{choice.Title}” is already in your catalog, so open it to see what the library has."), state);
                }

                // The chosen record itself, not a text search that may rank some other record first.
                var checkedChoice = await tools.CheckLibraryForChoiceAsync(choice, cancellationToken);
                return Keep(LibraryReply(checkedChoice, choice.Title, "Say “request it” to request it."), state);

            default:
                return await AfterTitlePlanAsync(
                    new ParsedConversationMessage(
                        ConversationCommandKind.Series, Text: choice.Title, SeriesName: choice.SeriesName ?? string.Empty,
                        SeriesAfterTitle: choice.Title, Action: ConversationActions.Next),
                    state, transport, scopeKey, cancellationToken);
        }
    }

    /// <summary>
    /// "what comes next?" about the book the last answer was about: the next strictly greater place in the retained series, read from
    /// the retained window with no provider call. Without a retained series or a single book in focus the person is asked, not guessed.
    /// </summary>
    private async Task<TurnPlan> NextAfterFocusPlanAsync(
        ConversationState state, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (!CatalogAnswerContextRef.TryParse(state.AnswerContext, out var reference) || reference is null)
        {
            return ExpiredChoicePlan(state);
        }

        if (reference.Subject is not { } subject)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "Which series do you mean? Tell me the series or ask for its latest book first."), state);
        }

        var anchor = state.FocusEntry?.ToReference() ?? reference.Anchor;
        if (anchor is null)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "Which book should I continue from? Reply with its number first."), state);
        }

        var filter = FilterOf(state);
        var request = new CatalogAnswerRequest
        {
            Operation = CatalogAnswerOperation.AfterAnchor,
            Subject = subject,
            Anchor = anchor,
            ContextRootId = reference.RootId,
            Order = reference.Order,
            Filter = filter
        };
        var (plan, _) = await AskAsync(request, filter, transport, scopeKey, state, requestIntent: false, formats: null, cancellationToken);
        return plan;
    }

    private static int? FocusIndex(ConversationState state)
    {
        if (state is { Phase: ConversationPhase.ChoosingFormat, SelectedIndex: { } selected } && selected >= 0 && selected < state.Choices.Count)
        {
            return selected;
        }

        return state.Choices.Count == 1 ? 0 : null;
    }

    private static string FormatPosition(decimal position) => position.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatPositionOrLast(decimal asked, SeriesLookupResult lookup) =>
        asked == SeriesPositionResolver.LastPosition && lookup.KnownPositions.Count > 0
            ? FormatPosition(lookup.KnownPositions[^1])
            : FormatPosition(asked);

    private async Task<TurnPlan> SearchPlanAsync(
        string query, string label, IReadOnlyList<RequestMediaType>? formats, bool requestIntent,
        ConversationState state, CancellationToken cancellationToken, bool tryInterpretIfEmpty = false, string? lead = null)
    {
        var found = await tools.SearchBooksAsync(query, options.MaxChoices, cancellationToken);

        // An optional model is asked to reword the search in two cases only: the catalog found nothing, or the message reads like
        // a sentence rather than a title and none of the results is that exact title (a fuzzy catalog will happily return five
        // unrelated books for a question). If it declines, fails, or its guess finds nothing, the ordinary answer below is unchanged.
        var interpret = tryInterpretIfEmpty && !found.SomeSourcesUnavailable &&
            (found.Choices.Count == 0 || (ConversationProse.LooksLikeProse(query) && !ConversationProse.HasExactTitle(found.Choices, query)));
        if (interpret && await InterpretedPlanAsync(query, formats, state, cancellationToken) is { } interpreted)
        {
            return interpreted;
        }

        if (found.Choices.Count == 0)
        {
            var none = found.SomeSourcesUnavailable
                ? $"I couldn't reach the catalog sources just now for “{label}”. Please try again in a minute."
                : $"I couldn't find anything for “{label}”. Try the title and author, or an ISBN.";
            return new TurnPlan(ConversationReply.Of(ConversationReplyKind.Text, none), ConversationState.Empty(), null);
        }

        var next = new ConversationState
        {
            Phase = ConversationPhase.ChoosingBook,
            Query = ConversationState.Clip(label),
            Choices = found.Choices.ToList(),
            FormatHint = formats?.ToList() ?? [],
            RequestIntent = requestIntent
        };
        var choices = found.Choices.Select((choice, index) => new ConversationChoice(index + 1, choice.Title, choice.Authors, choice.Detail)).ToArray();
        var lines = new StringBuilder(lead ?? $"I found these for “{label}”:");
        foreach (var choice in choices)
        {
            var authors = choice.Authors.Length > 0 ? $" — {choice.Authors}" : "";
            var detail = string.IsNullOrEmpty(choice.Detail) ? "" : $" ({choice.Detail})";
            lines.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n{choice.Number}. {choice.Title}{authors}{detail}");
        }

        var hint = formats is { Count: > 0 } ? $" as {FormatList(formats)}" : "";
        lines.Append(requestIntent
            ? $"\nReply with a number to request it{hint}, or “details 2” for more."
            : "\nReply with a number to pick one, “details 2” for more, or “request 2 audiobook”.");
        if (found.SomeSourcesUnavailable)
        {
            lines.Append("\n(Some catalog sources didn't respond, so this list may be incomplete.)");
        }

        return new TurnPlan(
            new ConversationReply(ConversationReplyKind.Choices, lines.ToString(), choices, [], ["1", "cancel"]),
            next, null);
    }

    /// <summary>
    /// A model's reading of an unrecognized message, turned into one of the same read-only replies the fixed grammar gives.
    /// The model can reword a search, route to the person's own status, or call the message off-topic. It cannot choose a
    /// format, pick a result, or request anything: those stay with the person's own words. Null means "carry on as before".
    /// </summary>
    private async Task<TurnPlan?> InterpretedPlanAsync(
        string text, IReadOnlyList<RequestMediaType>? formats, ConversationState state, CancellationToken cancellationToken)
    {
        if (interpreter is null || currentUser.UserId is not { } userId || text.Length < 12 ||
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3)
        {
            return null;
        }

        BookIntentInterpretation? interpretation;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            interpretation = await interpreter.InterpretAsync(userId, text, state.Query, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics?.StepCompleted(ConversationSteps.Interpreter, System.Diagnostics.Stopwatch.GetElapsedTime(started), "failed");
            return null;
        }

        diagnostics?.StepCompleted(
            ConversationSteps.Interpreter, System.Diagnostics.Stopwatch.GetElapsedTime(started), interpretation?.Intent.ToString() ?? "none");

        switch (interpretation?.Intent)
        {
            // The model never overrides the fixed rules: a message with a plain book cue is not off topic, and one that does not
            // mention requests is not a status question, whatever the model says. Both only decide a canned reply, so neither
            // clears the conversation: the books being discussed stay in focus.
            case BookIntent.OutOfDomain when ConversationParser.HasBookCue(text):
            case BookIntent.CheckStatus when !ConversationParser.MentionsRequests(text):
                if (workflow is not null) await workflow.ObserveAsync(interpretation.UsageRecordId, ModelWorkflowEffect.Abstained, cancellationToken);
                return null;

            case BookIntent.OutOfDomain:
                if (workflow is not null) await workflow.ObserveAsync(interpretation.UsageRecordId, ModelWorkflowEffect.IntentUsed, cancellationToken);
                return new TurnPlan(
                    ConversationReply.Of(ConversationReplyKind.OutOfDomain,
                        "I can only help with books: finding them, checking the library, and your book requests. " +
                        "Try “find The Hobbit”, “status”, or “help”."),
                    state, null);

            case BookIntent.CheckStatus:
                var status = await StatusReplyAsync(cancellationToken);
                if (workflow is not null) await workflow.ObserveAsync(interpretation.UsageRecordId, ModelWorkflowEffect.IntentUsed, cancellationToken);
                return new TurnPlan(status, state, null);

            case BookIntent.FindBook when interpretation.Title is { } title:
                var reworded = interpretation.Author is { } author ? $"{title} {author}" : title;
                if (string.Equals(reworded, text, StringComparison.OrdinalIgnoreCase))
                {
                    if (workflow is not null) await workflow.ObserveAsync(interpretation.UsageRecordId, ModelWorkflowEffect.NoChange, cancellationToken);
                    return null;
                }

                var plan = await SearchPlanAsync(
                    reworded, reworded, formats, requestIntent: false, state, cancellationToken,
                    lead: $"I wasn't sure what you meant, so I took a guess at “{reworded}”. I found these:");
                if (workflow is not null) await workflow.ObserveAsync(interpretation.UsageRecordId,
                    plan.Reply.Kind == ConversationReplyKind.Choices ? ModelWorkflowEffect.IntentUsed : ModelWorkflowEffect.NoChange, cancellationToken);
                return plan.Reply.Kind == ConversationReplyKind.Choices ? plan : null;

            default:
                if (workflow is not null) await workflow.ObserveAsync(interpretation?.UsageRecordId, ModelWorkflowEffect.Abstained, cancellationToken);
                return null;
        }
    }

    /// <summary>
    /// Whether the answer a list came from is still held for this reader. A list from an ordinary search has no such answer and is
    /// governed by the session alone; one from a catalog answer is also gone when its context expired or was evicted, however recently
    /// the reader last spoke.
    /// </summary>
    private bool ListLive(ConversationState state, string transport, string scopeKey)
    {
        if (state.AnswerContext is null) return true;
        return RootOf(state) is { } root && AnswerRootLive(transport, scopeKey, root);
    }

    private bool AnswerRootLive(string transport, string scopeKey, Guid root) =>
        CallerOf(transport, scopeKey) is { } caller && answerContexts.TryGetRoot(caller, root, clock.UtcNow) is not null;

    private async Task<TurnPlan> PickPlanAsync(
        ConversationState state, int number, IReadOnlyList<RequestMediaType>? formats, string transport, string scopeKey,
        CancellationToken cancellationToken)
    {
        if (state.Choices.Count == 0)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                "I don't have a list to pick from. Tell me a book to look for first."), state);
        }

        if (number > state.Choices.Count)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"Pick a number from 1 to {state.Choices.Count}, or say “cancel”."), state);
        }

        if (!ListLive(state, transport, scopeKey)) return ExpiredChoicePlan(state);

        var choice = state.Choices[number - 1];
        var chosen = formats is { Count: > 0 } ? formats : state.FormatHint is { Count: > 0 } ? state.FormatHint : null;
        if (chosen is null)
        {
            // The format step is part of the same list: it keeps the answer it came from (so it ends with it) and the list's identity.
            var next = new ConversationState
            {
                Phase = ConversationPhase.ChoosingFormat,
                Query = state.Query,
                Choices = state.Choices,
                SelectedIndex = number - 1,
                RequestIntent = state.RequestIntent,
                AnswerContext = state.AnswerContext,
                ListId = state.ListId
            };
            return new TurnPlan(
                ConversationReply.Of(ConversationReplyKind.AskFormat,
                    $"“{choice.Title}”{(choice.Authors.Length > 0 ? $" by {choice.Authors}" : "")}. Which format would you like: ebook, audiobook, or both?",
                    "ebook", "audiobook", "both", "cancel"),
                next, null);
        }

        return await RequestPlanAsync(choice, chosen, RootOf(state), cancellationToken);
    }

    private async Task<TurnPlan> FormatPlanAsync(
        ConversationState state, IReadOnlyList<RequestMediaType> formats, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (formats.Count == 0)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood, "Choose ebook, audiobook, or both."), state);
        }

        if (state is { Phase: ConversationPhase.ChoosingFormat, SelectedIndex: { } index } && index >= 0 && index < state.Choices.Count)
        {
            if (!ListLive(state, transport, scopeKey)) return ExpiredChoicePlan(state);
            return await RequestPlanAsync(state.Choices[index], formats, RootOf(state), cancellationToken);
        }

        return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
            state.Choices.Count > 0
                ? "Which book? Reply with its number first."
                : "Tell me a book to look for first, then I can request it."), state);
    }

    private async Task<TurnPlan> RequestPlanAsync(
        ConversationChoiceRef choice, IReadOnlyList<RequestMediaType> formats, Guid? answerRoot, CancellationToken cancellationToken)
    {
        // A record the library already holds needs no catalog write at all: its own saved id is the identity.
        Guid? workId = choice.SavedWorkId;
        if (workId is null && !string.IsNullOrEmpty(choice.ExternalId))
        {
            // Resolving a source record saves it to the catalog, which only a person who can still use chat may cause.
            if (await PrincipalWithdrawnAsync(cancellationToken)) return WithdrawnPlan();
            workId = await tools.ResolveWorkAsync(choice, cancellationToken);
        }

        if (workId is null)
        {
            return new TurnPlan(
                ConversationReply.Of(ConversationReplyKind.Unavailable,
                    $"I couldn't set up “{choice.Title}” right now because its catalog source isn't responding. Please try again shortly."),
                ConversationState.Empty(), null);
        }

        var distinct = formats.Distinct().ToArray();
        return new TurnPlan(
            ConversationReply.Of(ConversationReplyKind.Text, "Requesting…"), ConversationState.Empty(),
            new PendingRequest(workId.Value, distinct, choice.Title, answerRoot));
    }

    private static TurnPlan WithdrawnPlan() =>
        new(ConversationReply.Of(ConversationReplyKind.Unauthenticated, "Your Family Librarian account can no longer use chat."),
            ConversationState.Empty(), null);

    private async Task<ConversationReply> DetailsReplyAsync(
        ConversationState state, int number, string transport, string scopeKey, CancellationToken cancellationToken)
    {
        if (state.Choices.Count == 0 || number > state.Choices.Count)
        {
            return ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                state.Choices.Count == 0 ? "I don't have a list to show details from. Tell me a book to look for first."
                    : $"Pick a number from 1 to {state.Choices.Count}.");
        }

        if (!ListLive(state, transport, scopeKey)) return ExpiredChoicePlan(state).Reply;

        var details = await tools.GetBookDetailsAsync(state.Choices[number - 1], cancellationToken);
        return details is null
            ? ConversationReply.Of(ConversationReplyKind.Unavailable, "I couldn't load the details for that one right now.")
            : ConversationReply.Of(ConversationReplyKind.Details, details, number.ToString(System.Globalization.CultureInfo.InvariantCulture), "cancel");
    }

    private async Task<ConversationReply> LibraryReplyAsync(string query, CancellationToken cancellationToken) =>
        LibraryReply(await tools.CheckLibraryAsync(query, cancellationToken), query, $"Say “get {query}” to request it.");

    private static ConversationReply LibraryReply(ConversationLibraryResult? result, string query, string requestHint)
    {
        if (result is null)
        {
            return ConversationReply.Of(ConversationReplyKind.Library, $"I couldn't find “{query}” in the catalog to check the library for.");
        }

        var by = result.Authors.Length > 0 ? $" by {result.Authors}" : "";
        if (result.Incomplete)
        {
            // Ran out of time waiting on a source: that says nothing about whether the library has it.
            return ConversationReply.Of(ConversationReplyKind.Library, result.Authors.Length > 0
                ? $"I found “{result.Title}”{by}, but couldn't finish checking the library in time because a source didn't respond. Try again in a minute."
                : $"I couldn't finish checking the library for “{query}” in time because a source didn't respond. Try again in a minute.");
        }

        var text = result.OwnedFormats.Count > 0
            ? $"Yes: “{result.Title}”{by} is in the library as {string.Join(" and ", result.OwnedFormats)}."
            : $"Not yet: “{result.Title}”{by} isn't in the library. {requestHint}";
        if (result.SourcesUnavailable) text += " (Some library sources didn't respond.)";
        return ConversationReply.Of(ConversationReplyKind.Library, text);
    }

    private async Task<ConversationReply> StatusReplyAsync(CancellationToken cancellationToken)
    {
        var mine = await tools.GetMyRequestsAsync(cancellationToken);
        if (mine.Count == 0)
        {
            return ConversationReply.Of(ConversationReplyKind.Status, "You don't have any requests yet. Tell me a book to look for.");
        }

        var active = mine.Where(request => request.IsActive).Take(5).ToArray();
        var recent = mine.Where(request => !request.IsActive).Take(3).ToArray();
        var shown = active.Concat(recent).Select(request => new ConversationRequestSummary(
            request.Id, request.WorkTitle, string.Join(", ", request.Authors.Take(3)), Humanize(request.Status.ToString()),
            request.Formats.Select(format => $"{format.MediaType.ToString().ToLowerInvariant()}: {Humanize(format.Status.ToString())}").ToArray())).ToArray();
        var lines = new StringBuilder(active.Length > 0 ? "Your requests:" : "No open requests. Recent ones:");
        foreach (var item in shown)
        {
            lines.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n• {item.Title} — {item.Status} ({string.Join("; ", item.Formats)})");
        }

        return new ConversationReply(ConversationReplyKind.Status, lines.ToString(), [], shown, []);
    }

    private static ConversationReply Help() => ConversationReply.Of(
        ConversationReplyKind.Help,
        "I can help you find books and request them:\n" +
        "• Tell me a title, author or ISBN, e.g. “Find Weather by Jenny Offill”\n" +
        "• Pick a result by number, or “details 2”\n" +
        "• “request 2 audiobook” (ebook, audiobook or both)\n" +
        "• “the latest Expanse book”, “book 5 of The Expanse”, “what comes next?”\n" +
        "• “upcoming Murderbot books”, “when is <title> coming out?”\n" +
        "• “what has Becky Chambers written?” for an author's works\n" +
        "• “editions 1” lists a book's edition titles from its source\n" +
        "• “more” shows the next part of a long list\n" +
        "• “Do we have Dune?” checks the library\n" +
        "• “status” shows your requests\n" +
        "• “cancel” clears the conversation",
        "status", "help");

    private static TurnPlan Keep(ConversationReply reply, ConversationState state) => new(reply, state, null);

    private static ConversationTurnResult Replay(ConversationEventRecord seen, Guid userId, string transport, string scopeKey, string eventId)
    {
        if (seen.UserId != userId)
        {
            return Terminal(ConversationReplyKind.Unavailable, "That message couldn't be processed.");
        }

        var reply = DeserializeReply(seen.ReplyJson)
            ?? ConversationReply.Of(ConversationReplyKind.Text, "I'm still working on that message.");
        return new ConversationTurnResult(reply, Replayed: true, DeliveryPending: seen.DeliveredAtUtc is null,
            new ConversationEventKey(transport, scopeKey, eventId));
    }

    private static ConversationTurnResult Terminal(ConversationReplyKind kind, string text) =>
        new(ConversationReply.Of(kind, text), Replayed: false, DeliveryPending: false, null);

    /// <summary>
    /// The reply as it is recorded for a replay, within the bound. When the whole reply is too large the cards go first, because they
    /// restate the text; then the reply keeps its text with the numbered choices and the suggested replies, so a replay still says
    /// what the first reply said and still offers what it offered. Only a reply too large even then is cut to its opening text.
    /// </summary>
    private static string SerializeReply(ConversationReply reply)
    {
        var json = JsonSerializer.Serialize(reply);
        if (json.Length <= MaxReplyJsonLength) return json;

        var plain = reply with { Answers = null };
        json = JsonSerializer.Serialize(plain);
        if (json.Length <= MaxReplyJsonLength) return json;

        json = JsonSerializer.Serialize(plain with { Requests = [] });
        if (json.Length <= MaxReplyJsonLength) return json;

        return JsonSerializer.Serialize(ConversationReply.Of(reply.Kind, ConversationState.Clip(reply.Text, 1_500)) with { ListId = reply.ListId });
    }

    private static string NewListId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Gives a list its identity and puts it on the reply. A plan that kept the reader's list keeps its id, so a button drawn for it
    /// still works; a plan that offers a new list (its state was built fresh) gets a new id, which makes every earlier button stale.
    /// </summary>
    private static TurnPlan Stamp(TurnPlan plan)
    {
        if (plan.State.Phase == ConversationPhase.Idle) return plan;
        plan.State.ListId ??= NewListId();
        return plan with { Reply = plan.Reply with { ListId = plan.State.ListId } };
    }

    private static ConversationReply? DeserializeReply(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ConversationReply>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A key derived only from the message identity, so a replay cannot create a second request even if the processed-message record is gone.</summary>
    public static string RequestKey(string transport, string scopeKey, string eventId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{transport}\n{scopeKey}\n{eventId}")));

    private static string FormatList(IReadOnlyList<RequestMediaType> formats) =>
        string.Join(" and ", formats.Select(format => format.ToString().ToLowerInvariant()));

    public static string Humanize(string pascal)
    {
        var builder = new StringBuilder();
        foreach (var character in pascal)
        {
            if (char.IsUpper(character) && builder.Length > 0) builder.Append(' ');
            builder.Append(builder.Length == 0 ? character : char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private sealed record PendingRequest(Guid WorkId, IReadOnlyList<RequestMediaType> Formats, string Title, Guid? AnswerRoot = null);

    private sealed record TurnPlan(ConversationReply Reply, ConversationState State, PendingRequest? Request, bool ResetTurns = false);
}
