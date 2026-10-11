using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Requests;

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
    IClock clock) : IConversationService
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

        using var lease = await limiter.TryEnterAsync(userId, cancellationToken);
        if (lease is null)
        {
            return Terminal(ConversationReplyKind.Throttled, "You're sending messages quickly. Give me a moment, then try again.");
        }

        try
        {
            if (eventId is not null && await events.FindAsync(turn.Transport, scopeKey, eventId, cancellationToken) is { } seen)
            {
                return Replay(seen, userId, turn.Transport, scopeKey, eventId);
            }

            return await ProcessAsync(userId, turn.Transport, scopeKey, eventId, text, cancellationToken);
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

    private async Task<ConversationTurnResult> ProcessAsync(
        Guid userId, string transport, string scopeKey, string? eventId, string text, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var session = await sessions.FindAsync(userId, transport, scopeKey, cancellationToken)
            ?? new ConversationSession(userId, transport, scopeKey, ConversationState.Empty(), 0, now, now);
        if (now - session.LastActivityAtUtc > TimeSpan.FromMinutes(options.IdleExpiryMinutes))
        {
            session.State = ConversationState.Empty();
            session.TurnCount = 0;
            session.StartedAtUtc = now;
        }

        var parsed = ConversationParser.Parse(text);
        var countsAsTurn = parsed.Kind is not (ConversationCommandKind.Help or ConversationCommandKind.Cancel or ConversationCommandKind.Status
            or ConversationCommandKind.Empty);
        TurnPlan plan;
        if (countsAsTurn && session.TurnCount >= options.MaxTurnsPerSession)
        {
            plan = new TurnPlan(
                ConversationReply.Of(ConversationReplyKind.Throttled,
                    "We've covered a lot, so I've cleared this conversation. Send your search again to start fresh."),
                ConversationState.Empty(), null, ResetTurns: true);
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
                    ConversationState.Empty(), null);

            case ConversationCommandKind.OutOfDomain:
                return Keep(ConversationReply.Of(ConversationReplyKind.OutOfDomain,
                    "I can only help with books: finding them, checking the library, and your book requests. " +
                    "Try “find The Hobbit”, “status”, or “help”."), state);

            case ConversationCommandKind.Status:
                return Keep(await StatusReplyAsync(cancellationToken), state);

            case ConversationCommandKind.Library:
                return Keep(await LibraryReplyAsync(parsed.Text!, cancellationToken), state);

            case ConversationCommandKind.Isbn:
                return await SearchPlanAsync(parsed.Text!, $"ISBN {parsed.Text}", null, requestIntent: false, state, cancellationToken);

            case ConversationCommandKind.Search:
                return await SearchPlanAsync(parsed.Text!, parsed.Text!, parsed.Formats, parsed.RequestIntent, state, cancellationToken);

            case ConversationCommandKind.Details:
                return Keep(await DetailsReplyAsync(state, parsed.Number!.Value, cancellationToken), state);

            case ConversationCommandKind.Pick:
            case ConversationCommandKind.Request:
                return await PickPlanAsync(state, parsed.Number!.Value, parsed.Formats, cancellationToken);

            case ConversationCommandKind.Format:
                return await FormatPlanAsync(state, parsed.Formats!, cancellationToken);

            default:
                return Keep(ConversationReply.Of(ConversationReplyKind.NotUnderstood, "I didn't catch that. Say “help” to see what I can do."), state);
        }
    }

    private async Task<TurnPlan> SearchPlanAsync(
        string query, string label, IReadOnlyList<RequestMediaType>? formats, bool requestIntent,
        ConversationState state, CancellationToken cancellationToken)
    {
        var found = await tools.SearchBooksAsync(query, options.MaxChoices, cancellationToken);
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
        var lines = new StringBuilder($"I found these for “{label}”:");
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

    private async Task<ConversationReply> LibraryReplyAsync(string query, CancellationToken cancellationToken)
    {
        var result = await tools.CheckLibraryAsync(query, cancellationToken);
        if (result is null)
        {
            return ConversationReply.Of(ConversationReplyKind.Library, $"I couldn't find “{query}” in the catalog to check the library for.");
        }

        var by = result.Authors.Length > 0 ? $" by {result.Authors}" : "";
        var text = result.OwnedFormats.Count > 0
            ? $"Yes: “{result.Title}”{by} is in the library as {string.Join(" and ", result.OwnedFormats)}."
            : $"Not yet: “{result.Title}”{by} isn't in the library. Say “get {query}” to request it.";
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
