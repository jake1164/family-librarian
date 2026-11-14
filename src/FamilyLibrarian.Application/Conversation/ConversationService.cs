using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Requests;
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

public sealed class ConversationService(
    ICurrentUser currentUser,
    IConversationTools tools,
    IConversationSessionStore sessions,
    IConversationEventLog events,
    IConversationTransaction transaction,
    IConversationLimiter limiter,
    ConversationOptions options,
    IClock clock,
    IBookIntentInterpreter? interpreter = null,
    IConversationDiagnostics? diagnostics = null, IModelWorkflowObserver? workflow = null) : IConversationService
{
    private const int MaxScopeKeyLength = 200;
    private const int MaxEventIdLength = 255;
    private const int MaxReplyJsonLength = 8_000;

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
            var processed = await ProcessAsync(userId, turn.Transport, scopeKey, eventId, parsed, cancellationToken);
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

    private async Task<ConversationTurnResult> ProcessAsync(
        Guid userId, string transport, string scopeKey, string? eventId, ParsedConversationMessage parsed, CancellationToken cancellationToken)
    {
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
        else if (expired && parsed.Kind is ConversationCommandKind.Pick or ConversationCommandKind.Request or ConversationCommandKind.Format
                     or ConversationCommandKind.Details or ConversationCommandKind.Reference)
        {
            plan = Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                $"That search expired after {options.IdleExpiryMinutes} minutes without activity. Tell me the book again and I'll look it up."),
                ConversationState.Empty());
        }
        else
        {
            plan = await PlanAsync(parsed, session.State, cancellationToken);
        }

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

    private async Task<TurnPlan> PlanAsync(ParsedConversationMessage parsed, ConversationState state, CancellationToken cancellationToken)
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
                return await SeriesPlanAsync(parsed, state, cancellationToken);

            case ConversationCommandKind.Isbn:
                return await SearchPlanAsync(parsed.Text!, $"ISBN {parsed.Text}", null, requestIntent: false, state, cancellationToken);

            case ConversationCommandKind.Search:
                return await SearchPlanAsync(
                    parsed.Text!, parsed.Text!, parsed.Formats, parsed.RequestIntent, state, cancellationToken, tryInterpretIfEmpty: true);

            case ConversationCommandKind.Details:
                return Keep(await DetailsReplyAsync(state, parsed.Number!.Value, cancellationToken), state);

            case ConversationCommandKind.Reference:
                return await ReferencePlanAsync(parsed, state, cancellationToken);

            case ConversationCommandKind.Release:
                return await ReleasePlanAsync(parsed, state, cancellationToken);

            case ConversationCommandKind.Pick:
            case ConversationCommandKind.Request:
                return await PickPlanAsync(state, parsed.Number!.Value, parsed.Formats, cancellationToken);

            case ConversationCommandKind.Format:
                return await FormatPlanAsync(state, parsed.Formats!, cancellationToken);

            default:
                return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood, "I didn't catch that. Say “help” to see what I can do."), state);
        }
    }

    private async Task<TurnPlan> SeriesPlanAsync(ParsedConversationMessage parsed, ConversationState state, CancellationToken cancellationToken)
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
        if (parsed.SeriesPosition == SeriesPositionResolver.LastPosition && lookup.KnownPositions.Count < options.MinKnownPositionsForLatest)
        {
            var subject = string.IsNullOrWhiteSpace(parsed.SeriesName) ? lookup.SeriesName ?? string.Empty : parsed.SeriesName;
            var seen = lookup.KnownPositions.Count;
            return await SearchPlanAsync(
                subject, subject, parsed.Formats, parsed.RequestIntent, state, cancellationToken,
                lead: $"I can only see {seen} numbered {(seen == 1 ? "book" : "books")} of {lookup.SeriesName ?? subject} in the catalog, " +
                      $"which isn't enough to say which is the latest. Here's what I found for “{subject}”:");
        }

        var choices = lookup.Books
            .Where(book => book.ExternalId.Length is > 0 and <= ConversationState.MaxFieldLength)
            .Select(book => new ConversationChoiceRef(
                book.ProviderId, book.ExternalId, ConversationState.Clip(book.Title), ConversationState.Clip(book.Authors),
                ConversationState.Clip(book.Position is null ? book.SeriesName : $"{book.SeriesName} #{FormatPosition(book.Position.Value)}"),
                string.IsNullOrWhiteSpace(book.SeriesName) ? null : ConversationState.Clip(book.SeriesName)))
            .ToList();
        var label = parsed.SeriesAfterTitle is not null ? $"{lookup.SeriesName} after {parsed.SeriesAfterTitle}" : parsed.SeriesPosition is null ? $"{lookup.SeriesName} in order" : $"{lookup.SeriesName} #{FormatPositionOrLast(parsed.SeriesPosition.Value, lookup)}";
        var next = new ConversationState
        {
            Phase = ConversationPhase.ChoosingBook,
            Query = ConversationState.Clip(label),
            Choices = choices,
            FormatHint = parsed.Formats?.ToList() ?? [],
            RequestIntent = parsed.RequestIntent
        };
        if (choices.Count == 1 && (parsed.SeriesPosition is not null || parsed.SeriesAfterTitle is not null) && parsed.RequestIntent)
        {
            return await PickPlanAsync(next, 1, parsed.Formats, cancellationToken);
        }

        var shown = choices.Select((choice, index) => new ConversationChoice(index + 1, choice.Title, choice.Authors, choice.Detail)).ToArray();
        // "Latest" and "in order" are read from whatever books the catalog happened to show for the series, so they are never
        // worded as settled fact: a newer book, or an earlier one, may simply not have been listed.
        var latest = parsed.SeriesPosition == SeriesPositionResolver.LastPosition;
        var lines = new StringBuilder(parsed.SeriesAfterTitle is not null
            ? shown.Length == 1
                ? $"After {parsed.SeriesAfterTitle}, the next book in {lookup.SeriesName} is:"
                : $"After {parsed.SeriesAfterTitle}, more than one book is listed next in {lookup.SeriesName}:"
            : parsed.SeriesPosition is null
            ? $"{lookup.SeriesName}, in order (these are only the books the catalog showed me, so the list may be incomplete):"
            : latest
                ? shown.Length == 1
                    ? $"The highest-numbered book I can find in {lookup.SeriesName} is #{FormatPositionOrLast(parsed.SeriesPosition.Value, lookup)}. The catalog may not list every book, so there could be a newer one:"
                    : $"More than one book is listed as the highest-numbered in {lookup.SeriesName}, #{FormatPositionOrLast(parsed.SeriesPosition.Value, lookup)}. The catalog may not list every book, so there could be a newer one:"
            : shown.Length == 1
                ? $"In {lookup.SeriesName}, book {FormatPositionOrLast(parsed.SeriesPosition.Value, lookup)} is:"
                : $"More than one book is listed as book {FormatPositionOrLast(parsed.SeriesPosition.Value, lookup)} of {lookup.SeriesName}:");
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
    private async Task<TurnPlan> ReferencePlanAsync(ParsedConversationMessage parsed, ConversationState state, CancellationToken cancellationToken)
    {
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
                return await PickPlanAsync(state, index + 1, parsed.Formats, cancellationToken);

            case ConversationActions.Details:
                return Keep(await DetailsReplyAsync(state, index + 1, cancellationToken), state);

            case ConversationActions.Availability:
                // The chosen record itself, not a text search that may rank some other record first.
                var checkedChoice = await tools.CheckLibraryForChoiceAsync(choice, cancellationToken);
                return Keep(LibraryReply(checkedChoice, choice.Title, "Say “request it” to request it."), state);

            default:
                return await SeriesPlanAsync(
                    new ParsedConversationMessage(
                        ConversationCommandKind.Series, Text: choice.Title, SeriesName: choice.SeriesName ?? string.Empty,
                        SeriesAfterTitle: choice.Title, Action: ConversationActions.Next),
                    state, cancellationToken);
        }
    }

    private static int? FocusIndex(ConversationState state)
    {
        if (state is { Phase: ConversationPhase.ChoosingFormat, SelectedIndex: { } selected } && selected >= 0 && selected < state.Choices.Count)
        {
            return selected;
        }

        return state.Choices.Count == 1 ? 0 : null;
    }

    private const string UnpublishedNotice = "I can't see books that haven't been published yet";

    /// <summary>
    /// Unreleased books, release dates and release years are not in the catalog sources chat reads, so chat says so rather than
    /// returning search hits that look like an answer. A named title is still looked up so the person gets something useful.
    /// </summary>
    private async Task<TurnPlan> ReleasePlanAsync(ParsedConversationMessage parsed, ConversationState state, CancellationToken cancellationToken)
    {
        if (parsed is { Action: ConversationActions.When, Text: { Length: >= 2 } title })
        {
            return await SearchPlanAsync(
                title, title, null, requestIntent: false, state, cancellationToken,
                lead: $"I can't confirm release dates, and {UnpublishedNotice}. Here's what the catalog has for “{title}”:");
        }

        if (parsed is { Action: ConversationActions.Next, SeriesName: { Length: > 0 } series })
        {
            var lookup = await tools.FindSeriesBooksAsync(series, SeriesPositionResolver.LastPosition, null, options.MaxChoices, cancellationToken);
            if (lookup is { Kind: SeriesLookupKind.Found, Books.Count: > 0 } && lookup.KnownPositions.Count < options.MinKnownPositionsForLatest)
            {
                // Too little of the series is visible to name a highest book without misleading.
                var seen = lookup.KnownPositions.Count;
                return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                    $"{UnpublishedNotice}, so I can't say what's next in {lookup.SeriesName}. " +
                    $"I can only see {seen} numbered {(seen == 1 ? "book" : "books")} of it, so I can't tell you which is the latest either."), state);
            }

            if (lookup is { Kind: SeriesLookupKind.Found, Books.Count: > 0 })
            {
                var top = lookup.Books[0].Position is { } position ? $" #{FormatPosition(position)}" : string.Empty;
                return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                    $"{UnpublishedNotice}, so I can't say what's next in {lookup.SeriesName}. " +
                    $"The highest-numbered book I can find is{top}: {string.Join("; ", lookup.Books.Select(book => book.Title))}. " +
                    "The catalog may not list every book."), state);
            }

            return Keep(ConversationReply.Of(ConversationReplyKind.Text,
                $"{UnpublishedNotice}, so I can't say what's next in “{series}”. Ask for “the latest {series} book” or a title and I'll look it up."), state);
        }

        return Keep(ConversationReply.Of(ConversationReplyKind.Text,
            parsed.Action == ConversationActions.Year
                ? "I can't filter by release year, so I can't answer that. Ask for a title or a series and I'll look it up."
                : $"{UnpublishedNotice}, and the catalogs I use don't list upcoming releases. " +
                  "Ask for a title, or “the latest” book of a series, and I'll look it up."), state);
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

    private async Task<TurnPlan> PickPlanAsync(
        ConversationState state, int number, IReadOnlyList<RequestMediaType>? formats, CancellationToken cancellationToken)
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

        var choice = state.Choices[number - 1];
        var chosen = formats is { Count: > 0 } ? formats : state.FormatHint is { Count: > 0 } ? state.FormatHint : null;
        if (chosen is null)
        {
            var next = new ConversationState
            {
                Phase = ConversationPhase.ChoosingFormat,
                Query = state.Query,
                Choices = state.Choices,
                SelectedIndex = number - 1,
                RequestIntent = state.RequestIntent
            };
            return new TurnPlan(
                ConversationReply.Of(ConversationReplyKind.AskFormat,
                    $"“{choice.Title}”{(choice.Authors.Length > 0 ? $" by {choice.Authors}" : "")}. Which format would you like: ebook, audiobook, or both?",
                    "ebook", "audiobook", "both", "cancel"),
                next, null);
        }

        return await RequestPlanAsync(choice, chosen, cancellationToken);
    }

    private async Task<TurnPlan> FormatPlanAsync(
        ConversationState state, IReadOnlyList<RequestMediaType> formats, CancellationToken cancellationToken)
    {
        if (formats.Count == 0)
        {
            return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood, "Choose ebook, audiobook, or both."), state);
        }

        if (state is { Phase: ConversationPhase.ChoosingFormat, SelectedIndex: { } index } && index >= 0 && index < state.Choices.Count)
        {
            return await RequestPlanAsync(state.Choices[index], formats, cancellationToken);
        }

        return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood,
            state.Choices.Count > 0
                ? "Which book? Reply with its number first."
                : "Tell me a book to look for first, then I can request it."), state);
    }

    private async Task<TurnPlan> RequestPlanAsync(
        ConversationChoiceRef choice, IReadOnlyList<RequestMediaType> formats, CancellationToken cancellationToken)
    {
        var workId = await tools.ResolveWorkAsync(choice, cancellationToken);
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
            new PendingRequest(workId.Value, distinct, choice.Title));
    }

    private async Task<ConversationReply> DetailsReplyAsync(ConversationState state, int number, CancellationToken cancellationToken)
    {
        if (state.Choices.Count == 0 || number > state.Choices.Count)
        {
            return ConversationReply.Of(ConversationReplyKind.NotUnderstood,
                state.Choices.Count == 0 ? "I don't have a list to show details from. Tell me a book to look for first."
                    : $"Pick a number from 1 to {state.Choices.Count}.");
        }

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

    private static string SerializeReply(ConversationReply reply)
    {
        var json = JsonSerializer.Serialize(reply);
        return json.Length <= MaxReplyJsonLength
            ? json
            : JsonSerializer.Serialize(ConversationReply.Of(reply.Kind, ConversationState.Clip(reply.Text, 1_500)));
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

    private sealed record PendingRequest(Guid WorkId, IReadOnlyList<RequestMediaType> Formats, string Title);

    private sealed record TurnPlan(ConversationReply Reply, ConversationState State, PendingRequest? Request, bool ResetTurns = false);
}
