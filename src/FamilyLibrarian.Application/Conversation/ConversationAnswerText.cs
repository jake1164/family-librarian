using System.Globalization;
using System.Text;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Conversation;

/// <summary>
/// Turns one shared catalog answer into the reply text and cards both transports show. The plain text is complete on its own —
/// every qualification, what was checked and the way to continue are in it — because Matrix has no cards, buttons or links. The
/// cards add structure for the page from the same facts: nothing is added to one that is missing from the other.
/// </summary>
/// <remarks>
/// Text is composed against a hard character budget. When it does not fit, records are left out first and the text says how many,
/// then the notes are shortened; the headline, the qualification that changes how the answer must be read, what was checked and
/// the way to continue are never the parts that go. An internal code with no reviewed sentence is skipped, never echoed.
/// </remarks>
internal static class ConversationAnswerText
{
    /// <summary>The reviewed budget for the page: well under the 8,000-character replay bound.</summary>
    public const int WebMaxLength = 3_000;

    /// <summary>The reviewed budget for Matrix: under the formatter's 1,800-character presentation bound, leaving room for its own note.</summary>
    public const int MatrixMaxLength = 1_500;

    /// <summary>The one source whose selected-Work edition titles chat offers to expand; every other source answers unsupported.</summary>
    public const string EditionTitlesProvider = "openlibrary";

    /// <summary>The longest title a line shows. A longer one is cut with a visible mark; the record is still selected by its own id.</summary>
    public const int MaxDisplayTitle = 120;

    /// <summary>
    /// One rendered answer. <paramref name="Offered"/> is how many numbered items the text, the card and <paramref name="Choices"/>
    /// all show. <paramref name="ShownKeys"/> are the identity keys of the records the page showed (numbered or not) and
    /// <paramref name="Remaining"/> is how many records of the answer have still not been shown, so "more" can show exactly those
    /// whatever order a later page sorts into. A number is only ever drawn for an item the conversation will keep, so what is
    /// shown, saved and replayed is one list.
    /// </summary>
    public sealed record Rendered(
        string Text, IReadOnlyList<ConversationAnswerCard> Cards, IReadOnlyList<ConversationChoice> Choices, int Offered = 0,
        IReadOnlyList<string>? ShownKeys = null, int Remaining = 0);

    /// <summary>The operations whose lists can be read further with "more": the member, work and series lists.</summary>
    public static bool IsPageable(CatalogAnswerOperation operation) =>
        operation is CatalogAnswerOperation.SeriesMembers or CatalogAnswerOperation.AuthorWorks or CatalogAnswerOperation.AuthorSeries;

    /// <summary>
    /// A record can be numbered only when the conversation can keep whole the identity that selects it: a saved Work, or a source id
    /// short enough for the bounded state. A title is display text, never identity, so a long title does not make a record unusable.
    /// </summary>
    public static bool IsOfferable(CatalogAnswerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SavedWorkId is not null
            || entry.Source is { ProviderId.Length: > 0, ExternalId.Length: > 0 and <= ConversationSubjectChoice.MaxNativeIdLength };
    }

    private static readonly IReadOnlySet<string> NothingShown = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The identity key of a series: its saved id or source reference, never its name. A series with neither is keyed by its label.</summary>
    public static string KeyOf(CatalogAnswerSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        return series.SavedId is { } saved ? "series:work:" + saved.ToString("N")
            : series.Id is { } source ? "series:" + CatalogAnswerEntry.KeyOf(source)
            : "series:label:" + series.Label;
    }

    /// <summary>The keys of the answer's primary list (its series when it has any, otherwise its records), in the answer's order.</summary>
    public static IReadOnlyList<string> PrimaryKeys(CatalogAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return answer.Choices.Count > 0 ? []
            : answer.Series.Count > 0 ? answer.Series.Select(KeyOf).ToArray()
            : answer.Entries.Select(entry => entry.IdentityKey).ToArray();
    }

    /// <summary>The records of the answer not yet shown that can be numbered, in the order they would be.</summary>
    public static IReadOnlyList<CatalogAnswerEntry> OfferableEntries(CatalogAnswer answer, IReadOnlySet<string>? alreadyShown = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var seen = alreadyShown ?? NothingShown;
        return answer.Entries.Where(entry => !seen.Contains(entry.IdentityKey) && IsOfferable(entry)).ToArray();
    }

    /// <summary>
    /// The offered choices that can be kept for selection. One whose identity is too long to keep is left out of the numbering and
    /// does not stop the ones after it; selection is by identity, so the numbers need not match positions in the retained list.
    /// </summary>
    public static IReadOnlyList<CatalogAnswerChoice> OfferableChoices(CatalogAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return answer.Choices
            .Where(choice => ConversationSubjectChoice.From(choice) is not null)
            .Take(ConversationState.MaxSubjectChoices)
            .ToArray();
    }

    /// <summary>The author's series not yet shown that have an identity the state can keep, in the order they are numbered.</summary>
    public static IReadOnlyList<CatalogAnswerSeries> OfferableSeries(CatalogAnswer answer, IReadOnlySet<string>? alreadyShown = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var seen = alreadyShown ?? NothingShown;
        return answer.Series
            .Where(series => !seen.Contains(KeyOf(series)) && series.IsBound && SubjectChoiceOf(series) is not null)
            .Take(ConversationState.MaxSubjectChoices)
            .ToArray();
    }

    /// <summary>The selectable subject for a series, or null when its identity cannot be kept whole. The label is display text only.</summary>
    public static ConversationSubjectChoice? SubjectChoiceOf(CatalogAnswerSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var label = ConversationState.Ellipsize(series.Label, ConversationSubjectChoice.MaxLabelLength, "Untitled series");
        try
        {
            var reference = series.SavedId is { } saved && saved != Guid.Empty
                ? CatalogSubjectRef.Saved(CatalogSubjectKind.Series, label, saved)
                : series.Id is { Kind: ExternalReferenceEntityType.Series } source
                    ? CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, label, source)
                    : null;
            return reference is null ? null : ConversationSubjectChoice.From(new CatalogAnswerChoice(reference, null, series.PrimaryBooksCount, null, null));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// One answer as plain text, a card and the numbered picks. At most <paramref name="maxNumbered"/> items are numbered, and only
    /// the ones that fit the budget; the rest are counted as not shown, never numbered. <paramref name="alreadyShown"/> are the
    /// identity keys an earlier page showed: they are not shown again, and everything else is, in the answer's own order.
    /// </summary>
    public static Rendered ForAnswer(
        CatalogAnswer answer, int maxLength, int maxNumbered = ConversationState.MaxSubjectChoices, IReadOnlySet<string>? alreadyShown = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var seen = alreadyShown ?? NothingShown;
        var pageable = IsPageable(answer.Operation) && answer.Choices.Count == 0;
        var headline = Headline(answer) + (seen.Count > 0 ? " (continued)" : string.Empty);

        var items = new List<Item>();
        var number = 0;
        var primaryShown = 0;
        var unseenTotal = 0;
        if (answer.Choices.Count > 0)
        {
            var offerable = OfferableChoices(answer);
            unseenTotal = answer.Choices.Count;
            foreach (var choice in offerable.Take(Math.Max(0, maxNumbered)))
            {
                number++;
                var detail = string.IsNullOrWhiteSpace(choice.Disambiguation) ? KindName(choice.Reference.Kind) : choice.Disambiguation;
                var shown = new ConversationChoice(
                    number,
                    ConversationState.Clip(choice.Reference.Label),
                    ConversationState.Clip(detail),
                    string.IsNullOrWhiteSpace(choice.ProviderId) ? null : ConversationState.Clip(choice.ProviderId));
                primaryShown++;
                items.Add(new Item($"{number}. {shown.Title} ({shown.Authors})", new ConversationAnswerCardLine(shown.Title, shown.Authors, [], null), shown, null));
            }
        }
        else if (answer.Series.Count > 0)
        {
            var unseen = answer.Series.Where(series => !seen.Contains(KeyOf(series))).ToArray();
            unseenTotal = unseen.Length;
            foreach (var series in unseen)
            {
                var selectable = series.IsBound && SubjectChoiceOf(series) is not null;
                if (selectable && number >= Math.Max(0, maxNumbered)) break;
                primaryShown++;
                if (!selectable)
                {
                    // Listed so the reader sees it, and said plainly: it cannot be opened from here. It never blocks the rows after it.
                    items.Add(UnnumberedSeries(series));
                    continue;
                }

                number++;
                var shown = new ConversationChoice(number, SeriesTitle(series), string.Empty, "series");
                var facts = SeriesFacts(series);
                var href = series.SavedId is { } saved && saved != Guid.Empty ? "/series/" + saved.ToString("D", CultureInfo.InvariantCulture) : null;
                items.Add(new Item(
                    $"{number}. {shown.Title} — {string.Join(" · ", facts)}",
                    new ConversationAnswerCardLine($"{number}. {shown.Title}", null, facts, href),
                    shown,
                    KeyOf(series)));
            }
        }

        if (answer.Choices.Count == 0 && answer.Series.Count == 0)
        {
            var unseen = answer.Entries.Where(entry => !seen.Contains(entry.IdentityKey)).ToArray();
            unseenTotal = unseen.Length;
            foreach (var entry in unseen)
            {
                var offerable = IsOfferable(entry);
                if (offerable && number >= Math.Max(0, maxNumbered)) break;
                var line = EntryLine(entry, offerable ? number + 1 : null, answer.Order);
                if (offerable) number++;
                primaryShown++;
                items.Add(new Item(
                    line.Text, ToCard(line), offerable ? new ConversationChoice(number, DisplayTitle(entry), line.Authors ?? string.Empty, null) : null,
                    entry.IdentityKey));
            }
        }
        else if (answer.Entries.Count > 0 && answer.Choices.Count == 0)
        {
            // Records that came with the series are shown as plain lines: only the series are numbered, so a number has one meaning.
            foreach (var entry in answer.Entries)
            {
                var line = EntryLine(entry, null, answer.Order);
                items.Add(new Item(line.Text, ToCard(line), null, null));
            }
        }

        var groups = answer.Choices.Count > 0 || seen.Count > 0 ? [] : Pack(answer).Groups;
        var notes = Notes(answer, number > 0);
        var extraOmitted = Math.Max(0, unseenTotal - primaryShown);

        var text = Render(
            headline, items.Select(item => item.Text).ToArray(), extraOmitted, pageable, answer.ContinuationToken is not null,
            [.. groups], notes, maxLength, out var shownItems, out var shownGroups);
        var visible = items.Take(shownItems).ToArray();
        var offered = visible.Select(item => item.Pick).OfType<ConversationChoice>().ToArray();
        var shownKeys = visible.Select(item => item.Key).OfType<string>().ToArray();
        var cardGroups = groups.Take(shownGroups)
            .Select(group => new ConversationAnswerCardGroup(group.Heading, group.Lines.Select(ToCard).ToArray()))
            .ToArray();
        var card = new ConversationAnswerCard(
            "answer", headline, visible.Select(item => item.Card).ToArray(), cardGroups, notes,
            SubjectRoute(answer.Subject));
        return new Rendered(text, [card], offered, offered.Length, shownKeys, Math.Max(0, unseenTotal - shownKeys.Length));
    }

    private sealed record Item(string Text, ConversationAnswerCardLine Card, ConversationChoice? Pick, string? Key);

    private static Item UnnumberedSeries(CatalogAnswerSeries series)
    {
        var facts = SeriesFacts(series);
        var note = series.IsBound ? " — its id is too long to keep, so it can't be opened here" : " — no series id, so it can't be opened here";
        return new Item(
            $"• {SeriesTitle(series)} — {string.Join(" · ", facts)}{note}",
            new ConversationAnswerCardLine("• " + SeriesTitle(series), null, facts, null),
            null,
            KeyOf(series));
    }

    private static string SeriesTitle(CatalogAnswerSeries series) =>
        ConversationState.Ellipsize(series.Label, MaxDisplayTitle, "Untitled series");

    /// <summary>Whether the author-to-series link is the source's own statement or was worked out here, said on every line.</summary>
    private static List<string> SeriesFacts(CatalogAnswerSeries series)
    {
        var facts = new List<string>
        {
            series.Relationship.Basis == RelationshipBasis.Derived ? "worked out from the author's works" : "stated by the source"
        };
        if (series.PrimaryBooksCount is { } count) facts.Add(count == 1 ? "1 main book" : $"{count} main books");
        if (series.IsCompleted is { } completed) facts.Add(completed ? "complete" : "ongoing");
        return facts;
    }

    private static string DisplayTitle(CatalogAnswerEntry entry) =>
        ConversationState.Ellipsize(entry.Title, MaxDisplayTitle, "Untitled record");

    public static Rendered ForEditions(CatalogEditionsAnswer answer, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var heading = answer.Editions.Count == 0
            ? $"Edition titles for “{answer.WorkTitle}”"
            : $"Edition titles for “{answer.WorkTitle}” from {answer.ProviderId ?? "its source"}";
        var lines = answer.Editions
            .Select((edition, index) => new CardLine(
                $"{index + 1}. {edition.Title} ({LanguageText(edition.Language)})",
                null,
                [edition.Language is { Length: > 0 } language ? $"Language: {language}" : "Language not stated",
                 edition.EditionId is { Length: > 0 } id ? $"Edition: {id}" : "No edition id stated",
                 $"From: {edition.SourceField}"],
                null,
                string.Empty))
            .ToArray();
        var notes = new List<string>();
        foreach (var qualifier in answer.Qualifiers) AddSentence(notes, QualifierText(qualifier));
        foreach (var reason in answer.Reasons) AddSentence(notes, ReasonText(reason));
        notes.Add("These are edition titles as the source reports them: not alternative titles, translations or identity proof.");
        notes.Add(CoverageText(answer.Usage.ProviderReads));
        return new Rendered(
            Render(heading, lines.Select(line => line.Text).ToArray(), 0, false, false, [], [.. notes], maxLength, out var shown, out _),
            [new ConversationAnswerCard("editions", heading, lines.Take(shown).Select(ToCard).ToArray(), [], notes)],
            []);
    }

    private static string KindName(CatalogSubjectKind kind) => kind switch
    {
        CatalogSubjectKind.Author => "author",
        CatalogSubjectKind.Series => "series",
        CatalogSubjectKind.Book => "book",
        _ => "title"
    };

    /// <summary>
    /// Adds a qualification sentence from a stable code. Unknown codes are skipped: an internal code is never shown as prose.
    /// </summary>
    public static void AddSentence(List<string> notes, string? sentence)
    {
        if (string.IsNullOrEmpty(sentence) || notes.Contains(sentence, StringComparer.Ordinal)) return;
        notes.Add(sentence);
    }

    public static string? QualifierText(string code) => code switch
    {
        CatalogAnswerCodes.IncompleteCoverage => "Only part of what exists could be checked, so this is the answer within what was checked.",
        CatalogAnswerCodes.KnownLocalOnly => "This comes from your own catalog only, not from the metadata sources.",
        CatalogAnswerCodes.DateBasisUnverified => "It is not stated whether that date is the original publication or an edition's.",
        CatalogAnswerCodes.StatusUnverified => "The source did not state whether it is published.",
        CatalogAnswerCodes.TiedOrOverlappingDates => "Two entries share or overlap that date, so both are shown.",
        CatalogAnswerCodes.TiedPosition => "More than one book states that position, so both are shown.",
        CatalogAnswerCodes.UndatedEntriesPresent => "Entries with no stated date are listed apart.",
        CatalogAnswerCodes.UnnumberedEntriesPresent => "Entries that state no place in this order are listed apart.",
        CatalogAnswerCodes.ClassUnverified => "What kind of book some entries are was not verified.",
        CatalogAnswerCodes.RoleUnverified => "Whether some entries are main-series books was not verified.",
        CatalogAnswerCodes.LanguageUnverified => "The language was not stated for some entries.",
        CatalogAnswerCodes.PositionConflict => "A book states more than one place in this series.",
        CatalogAnswerCodes.StatusDateConflict => "The source's own status and date disagree, so that entry is not counted as published or upcoming.",
        CatalogAnswerCodes.ConflictingDates => "The sources state different dates.",
        CatalogAnswerCodes.NextKnownNotProvenImmediate => "This is the next one I can see, not proof there is nothing between.",
        CatalogAnswerCodes.ReleaseDateAttributed => "That date is what a source attributes to it, not a confirmed release.",
        CatalogAnswerCodes.ListNotExhaustive => "This is what I could check, not a complete list.",
        CatalogAnswerCodes.DerivedEdges => "The series were worked out from the author's works, not stated by the source.",
        CatalogAnswerCodes.LegacyPrecision => "A stored date had no precision recorded, so its widest reading is used.",
        CatalogAnswerCodes.SourceGaps => "Some sources did not answer, so this may be incomplete.",
        CatalogAnswerCodes.IncludesTodayInterval => "A date falls in a period that includes today, so it is not certainly published or upcoming.",
        CatalogAnswerCodes.EditionNotOriginal => "An edition's date is not the original publication date.",
        CatalogAnswerCodes.MultipleSourceRecords => "More than one source record fits.",
        CatalogAnswerCodes.ClassNotOriginal => "Records the source classes as a collection, abridgement or adaptation are listed apart.",
        CatalogAnswerCodes.CreditUnverified => "Some entries could not be confirmed as that author's work.",
        _ => null
    };

    /// <summary>
    /// The reason sentence for a clarification, worded for what is actually offered: a name is never an identity, so a lone
    /// series is still confirmed, but the reply does not claim several matched when only one did.
    /// </summary>
    public static string? ReasonText(string code, IReadOnlyList<CatalogAnswerChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return code == CatalogAnswerCodes.SeriesChoices &&
               choices.Count(choice => choice.Reference.Kind == CatalogSubjectKind.Series) == 1
            ? "One series matches that name, so I need you to confirm it."
            : ReasonText(code);
    }

    public static string? ReasonText(string code) => code switch
    {
        CatalogAnswerCodes.OrderAmbiguous => "The books are numbered in more than one order, so I need to know which one you mean.",
        CatalogAnswerCodes.NamesakeChoices => "More than one person is listed under that name.",
        CatalogAnswerCodes.SeriesChoices => "More than one series matches that name.",
        CatalogAnswerCodes.AuthorChoices => "That name belongs to an author, so the question is about their works or one of their series.",
        CatalogAnswerCodes.NoDatedPublishedEntry => "Nothing I checked states a publication date.",
        CatalogAnswerCodes.NoFutureDatedEntry => "Nothing I checked has a publication date still to come.",
        CatalogAnswerCodes.AnchorNotFound => "I couldn't find the book to continue from.",
        CatalogAnswerCodes.AnchorPositionUnknown => "That book states no place in the series.",
        CatalogAnswerCodes.AnchorPositionConflict => "That book states more than one place in the series.",
        CatalogAnswerCodes.NoLaterKnownEntry => "Nothing I checked comes after it.",
        CatalogAnswerCodes.NoExactTitleMatch => "No record I checked has exactly that title.",
        CatalogAnswerCodes.NoEntries => "The entries I checked held nothing to list.",
        CatalogAnswerCodes.SubjectRequired => "I need to know which series or author you mean.",
        CatalogAnswerCodes.SubjectInvalid => "That question doesn't apply to the book or series in focus.",
        CatalogAnswerCodes.OperationNotSupported => "That isn't something I can look up.",
        CatalogAnswerCodes.EditionTitlesUnavailable => "That source can't list edition titles.",
        CatalogAnswerCodes.NoEligibleSource => "No metadata source is available for that.",
        CatalogAnswerCodes.ContextExpiredReason => "That earlier list has expired, so I won't guess at it. Ask again and I'll look it up.",
        CatalogAnswerCodes.ContextNotRetained => "I couldn't keep that list for follow-up questions.",
        CatalogAnswerCodes.AdmissionRefused => "There are a lot of lookups running. Give it a moment and try again.",
        CatalogAnswerCodes.DeadlineReached => "It took too long to finish, so this is only what I had seen by then.",
        CatalogAnswerCodes.SeedNoUsableRecords => "I couldn't find a record to start from.",
        CatalogAnswerCodes.ClassFilterApplied => "Entries whose kind of book is unknown were left out of the filtered list.",
        CatalogAnswerCodes.AuthorIdsUnavailable => "That source gives no author ids, so I can't tell two people of the same name apart.",
        CatalogAnswerCodes.SeriesClaimsUnsupported => "That source doesn't state which series its books belong to.",
        CatalogAnswerCodes.NoChoicesFound => "I couldn't find anything to choose from.",
        CatalogAnswerCodes.ContinuationUnavailable => "There's no next page to read from that list.",
        CatalogAnswerCodes.ContextWindowCut => "The list was longer than I can keep for follow-ups, so it is not offered any further.",
        _ => null
    };

    private sealed record CardLine(string Text, string? Authors, IReadOnlyList<string> Facts, string? Href, string Title)
    {
        public string? TitleOrNull => Title.Length == 0 ? null : Title;
    }

    private sealed record GroupBlock(string Heading, IReadOnlyList<CardLine> Lines);

    private sealed record Packed(IReadOnlyList<CardLine> Entries, IReadOnlyList<GroupBlock> Groups);

    private static Packed Pack(CatalogAnswer answer)
    {
        var entries = answer.Entries
            .Select((entry, index) => EntryLine(entry, index + 1, answer.Order))
            .ToArray();
        var groups = answer.Groups
            .Where(group => group.Entries.Count > 0)
            .Select(group => new GroupBlock(GroupHeading(group.Code), group.Entries.Select(entry => EntryLine(entry, null, answer.Order)).ToArray()))
            .ToArray();
        return new Packed(entries, groups);
    }

    private static CardLine EntryLine(CatalogAnswerEntry entry, int? number, SeriesOrderingKind order)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var facts = new List<string>();
        var positions = entry.Positions
            .Where(claim => claim.Sort is not null)
            .Select(claim => "book " + claim.Sort!.Value.ToString("0.##", CultureInfo.InvariantCulture) +
                             (claim.Ordering is { } claimed && claimed != SeriesOrderingKind.Unspecified ? $" ({OrderName(claimed)})" : string.Empty))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (positions.Length > 0) facts.Add(string.Join(", ", positions));
        facts.Add(DateText(entry));
        if (!string.IsNullOrWhiteSpace(entry.Language)) facts.Add(entry.Language.Trim());
        if (entry.Class != CatalogWorkClass.Unknown) facts.Add(ClassName(entry.Class));
        if (!string.IsNullOrWhiteSpace(entry.SeriesLabel)) facts.Add(entry.SeriesLabel!);

        var authors = string.Join(", ", entry.Credits.Where(credit => credit.IsAuthorRole).Select(credit => credit.Label).Take(3));
        var prefix = number is { } shown ? $"{shown}. " : "• ";
        var shownTitle = DisplayTitle(entry);
        var text = new StringBuilder(prefix).Append(shownTitle);
        if (authors.Length > 0) text.Append(" — ").Append(authors);
        text.Append(" · ").Append(string.Join(" · ", facts));
        var href = entry.SavedWorkId is { } saved ? "/works/" + saved.ToString("D", CultureInfo.InvariantCulture) : null;
        var title = number is { } plain ? $"{plain}. {shownTitle}" : "• " + shownTitle;
        return new CardLine(text.ToString(), authors.Length > 0 ? authors : null, facts, href, title);
    }

    private static string GroupHeading(string code) => code switch
    {
        "unnumbered" => "No place stated in this order:",
        "class-not-original" => "Collections, abridgements and other non-original records:",
        "undated" => "Announced with no date stated:",
        "other-roles" => "Credited in another role (editor, translator, illustrator):",
        "credit-unverified" => "Credited in a way that didn't confirm this author:",
        "role-unverified" => "Main-series place not verified:",
        "class-unverified" => "Kind of book not verified:",
        "language-unverified" => "Language not stated:",
        "excluded-by-filter" => "Left out by the filter you asked for:",
        "ordering-unstated" => "Stated with no order named:",
        "status-date-conflict" => "Status and date disagree:",
        _ => "Also seen:"
    };

    private static string Headline(CatalogAnswer answer)
    {
        var subject = answer.Subject?.Label;
        var many = answer.Entries.Count > 1;
        return answer.Outcome switch
        {
            CatalogAnswerOutcome.Clarification when answer.Choices.Count > 0 => "I need one more choice before I can answer:",
            CatalogAnswerOutcome.Clarification when answer.AvailableOrders.Count > 0 => "These books are numbered in more than one order. Which do you mean?",
            CatalogAnswerOutcome.Clarification => "I need a little more to go on:",
            CatalogAnswerOutcome.ContextExpired => "That earlier list is no longer available.",
            CatalogAnswerOutcome.Unsupported => "I can't answer that one with the sources I have.",
            CatalogAnswerOutcome.Unavailable => "That source isn't available right now.",
            CatalogAnswerOutcome.Throttled => "There are a lot of lookups running right now.",
            CatalogAnswerOutcome.Refused => "A lookup is already running, so I didn't start another.",
            CatalogAnswerOutcome.TimedOut => "That lookup ran out of time.",
            CatalogAnswerOutcome.Failed => "That lookup failed.",
            CatalogAnswerOutcome.NoVerifiedMatch => NoMatchHeadline(answer, subject),
            CatalogAnswerOutcome.InsufficientEvidence when answer.Entries.Count == 0 => "I can't answer that with what I could check.",
            _ => subject is null ? "Here is what I found:" : AnsweredHeadline(answer, subject, many)
        };
    }

    private static string NoMatchHeadline(CatalogAnswer answer, string? subject) => answer.Operation switch
    {
        CatalogAnswerOperation.Position when answer.Subject is not null =>
            $"No book I checked claims book {FormatPosition(answer.Position)} of {subject}.",
        CatalogAnswerOperation.AfterAnchor when answer.Anchor is not null => $"Nothing I checked comes after “{answer.Anchor.Label}”.",
        CatalogAnswerOperation.LatestPublished when subject is not null => $"I found no published book in {subject} in what I checked.",
        CatalogAnswerOperation.UpcomingReleases when subject is not null => $"I found no book in {subject} with a date still to come.",
        CatalogAnswerOperation.PublicationForTitle => "No record I checked has exactly that title.",
        _ => "I found nothing in what I checked."
    };

    private static string AnsweredHeadline(CatalogAnswer answer, string subject, bool many) => answer.Operation switch
    {
        CatalogAnswerOperation.LatestPublished => many
            ? $"The latest published books I can find in {subject} share that date:"
            : $"The latest published book I can find in {subject} is:",
        CatalogAnswerOperation.Position => many
            ? $"More than one book states book {FormatPosition(answer.Position)} of {subject}:"
            : $"Book {FormatPosition(answer.Position)} of {subject} is:",
        CatalogAnswerOperation.AfterAnchor => answer.Anchor is null
            ? $"Here is the next book in {subject}:"
            : $"The next book after “{answer.Anchor.Label}” in {subject} is:",
        CatalogAnswerOperation.UpcomingReleases => many
            ? $"Books in {subject} with a publication date still to come:"
            : $"The one book in {subject} with a publication date still to come:",
        CatalogAnswerOperation.NextRelease => $"The next release I can find in {subject} is:",
        CatalogAnswerOperation.PublicationForTitle => "What the sources say about its publication date:",
        CatalogAnswerOperation.AuthorWorks => "Known works by this author:",
        CatalogAnswerOperation.AuthorSeries => "Series this author is credited on:",
        CatalogAnswerOperation.SeriesMembers => many ? $"The books I can see in {subject}, in order:" : $"The one book I can see in {subject}:",
        _ => "Here is what I found:"
    };

    private static string[] Notes(CatalogAnswer answer, bool anyNumbered)
    {
        var notes = new List<string>();
        if (answer.AvailableOrders.Count > 1)
        {
            for (var index = 0; index < answer.AvailableOrders.Count; index++)
            {
                notes.Add($"{index + 1}. {OrderName(answer.AvailableOrders[index])} order");
            }
        }

        foreach (var qualifier in answer.Qualifiers) AddSentence(notes, QualifierText(qualifier));
        foreach (var reason in answer.Reasons) AddSentence(notes, ReasonText(reason, answer.Choices));
        var coverage = CoverageText(answer.Coverage);
        AddSentence(notes, coverage);
        if (answer.AvailableOrders.Count > 1) notes.Add("Reply with the number of the order you mean.");
        AddSentence(notes, ContinuationText(answer, anyNumbered));
        return [.. notes];
    }

    private static string? ContinuationText(CatalogAnswer answer, bool anyNumbered)
    {
        if (answer.Context is null && answer.Outcome is not (CatalogAnswerOutcome.Answered or CatalogAnswerOutcome.Qualified)) return null;
        if (answer.Outcome is CatalogAnswerOutcome.Clarification && answer.Choices.Count > 0) return "Reply with the number you mean.";
        if (answer.Outcome is CatalogAnswerOutcome.Clarification) return null;
        if (answer.Series.Count > 0) return anyNumbered ? "Reply with a number to see that series' books." : null;
        if (answer.Entries.Count == 0 || !anyNumbered) return null;
        var editions = answer.Entries.Any(entry =>
            entry.Source is { ProviderId: EditionTitlesProvider, Kind: ExternalReferenceEntityType.Work });
        return editions
            ? "Reply with a number for details or to request it, or say “editions 1” for its edition titles."
            : "Reply with a number for details or to request it.";
    }

    private static string CoverageText(CatalogAnswerCoverage coverage)
    {
        var parts = coverage.Sources
            .Select(source => source.State switch
            {
                CatalogSourceState.Checked => $"{source.ProviderId} answered",
                CatalogSourceState.SavedCatalog => "your own catalog",
                CatalogSourceState.NotQueried => $"{source.ProviderId} was not asked",
                CatalogSourceState.Unsupported => $"{source.ProviderId} can't answer that",
                CatalogSourceState.Unavailable => $"{source.ProviderId} is unavailable",
                CatalogSourceState.Throttled => $"{source.ProviderId} is rate-limiting",
                CatalogSourceState.TimedOut => $"{source.ProviderId} timed out",
                _ => $"{source.ProviderId} failed"
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (parts.Length == 0) return string.Empty;
        var text = "Checked: " + string.Join("; ", parts) + ".";
        if (coverage.Truncated) text += " The list had to be cut.";
        else if (coverage.MoreAvailable) text += " There is more to read next time.";
        return text;
    }

    private static string CoverageText(int reads) => reads == 0 ? "No source was contacted." : string.Empty;

    private static string Render(
        string headline, string[] entryLines, int extraOmitted, bool pageable, bool tokenHeld, IReadOnlyList<GroupBlock> groups, string[] notes,
        int maxLength, out int shownEntries, out int shownGroups)
    {
        var more = new MoreHint(extraOmitted, pageable, tokenHeld);
        for (var keepEntries = entryLines.Length; keepEntries >= 0; keepEntries--)
        {
            for (var keepGroups = groups.Count; keepGroups >= 0; keepGroups--)
            {
                var text = Assemble(headline, entryLines, keepEntries, groups, keepGroups, notes, more);
                if (text.Length <= maxLength)
                {
                    shownEntries = keepEntries;
                    shownGroups = keepGroups;
                    return text;
                }
            }
        }

        // Entries and groups are all gone and the notes alone are still too long: shorten the notes, never the headline.
        for (var keepNotes = notes.Length; keepNotes >= 1; keepNotes--)
        {
            var text = Assemble(headline, entryLines, 0, groups, 0, notes.Take(keepNotes).ToArray(), more);
            if (text.Length <= maxLength)
            {
                shownEntries = 0;
                shownGroups = 0;
                return text;
            }
        }

        shownEntries = 0;
        shownGroups = 0;
        return Fit([headline], maxLength);
    }

    /// <summary>What a reader needs to know to ask for the rest: how many were left out and whether "more" can reach them.</summary>
    private sealed record MoreHint(int ExtraOmitted, bool Pageable, bool TokenHeld);

    private static string Assemble(
        string headline, string[] entryLines, int keepEntries, IReadOnlyList<GroupBlock> groups, int keepGroups,
        string[] notes, MoreHint more)
    {
        var lines = new List<string> { headline };
        for (var index = 0; index < keepEntries; index++) lines.Add(entryLines[index]);
        var omitted = entryLines.Length - keepEntries + more.ExtraOmitted;
        if (omitted > 0) lines.Add($"(+{omitted} more {(omitted == 1 ? "entry" : "entries")} not shown.)");
        if (more.Pageable && (omitted > 0 || more.TokenHeld)) lines.Add("Say “more” to see the next ones.");
        for (var index = 0; index < keepGroups; index++)
        {
            lines.Add(groups[index].Heading);
            lines.AddRange(groups[index].Lines.Select(line => line.Text));
        }

        if (keepGroups < groups.Count && notes.Length > 0) lines.Add(string.Empty);
        foreach (var note in notes) lines.Add(note);
        return string.Join('\n', lines.Where(line => line.Length > 0));
    }

    /// <summary>Joins lines under a hard bound, cutting only at a line break so a sentence is never shown half-written.</summary>
    private static string Fit(string[] lines, int maxLength)
    {
        var joined = string.Join('\n', lines.Where(line => line.Length > 0));
        if (joined.Length <= maxLength) return joined;
        var cut = joined.LastIndexOf('\n', Math.Max(0, maxLength - 1));
        return (cut > 0 ? joined[..cut] : joined[..Math.Max(0, maxLength - 1)]).TrimEnd() + "\n…";
    }

    private static ConversationAnswerCard Card(
        CatalogAnswer answer, string headline, IReadOnlyList<ConversationAnswerCardLine> lines,
        IReadOnlyList<ConversationAnswerCardGroup> groups, IReadOnlyList<string> notes) =>
        new(
            "answer",
            headline,
            lines,
            groups,
            notes,
            SubjectRoute(answer.Subject));

    /// <summary>
    /// The application page for the saved record an answer is about, by what that record actually is: a saved id is only ever
    /// turned into the route of its own kind, so an author's id can never become a series address. A subject with no saved
    /// id, or of a kind with no page of its own (a title), has no link.
    /// </summary>
    private static string? SubjectRoute(CatalogSubjectRef? subject) => subject is { SavedId: { } id } && id != Guid.Empty
        ? subject.Kind switch
        {
            CatalogSubjectKind.Series => "/series/" + id.ToString("D", CultureInfo.InvariantCulture),
            CatalogSubjectKind.Author => "/authors/" + id.ToString("D", CultureInfo.InvariantCulture),
            _ => null
        }
        : null;

    private static ConversationAnswerCardLine ToCard(CardLine line) =>
        new(line.TitleOrNull ?? line.Text, line.Authors, line.Facts, line.Href);

    private static string DateText(CatalogAnswerEntry entry)
    {
        var publication = entry.Publication;
        if (publication.State == PublicationDateState.KnownUndated) return "announced, no date stated";
        if (publication.State != PublicationDateState.Dated || publication.Date is not { } date) return "no date stated";

        var precision = publication.Precision ?? DatePrecision.Day;
        var written = precision switch
        {
            DatePrecision.Day => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DatePrecision.Month => date.ToString("yyyy-MM", CultureInfo.InvariantCulture) + " (month only)",
            _ => date.ToString("yyyy", CultureInfo.InvariantCulture) + " (year only)"
        };
        var basis = publication.Basis switch
        {
            DateBasis.Original => "original publication",
            DateBasis.EditionDerived => "edition date",
            _ => "basis not stated"
        };
        var status = publication.SourceStatus == PublicationStatus.Forthcoming ? ", source says forthcoming" : string.Empty;
        return $"{written}, {basis}{status}";
    }

    private static string LanguageText(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "language not stated" : language.Trim();

    private static string FormatPosition(decimal? position) =>
        position is { } value ? value.ToString("0.##", CultureInfo.InvariantCulture) : "that position";

    private static string OrderName(SeriesOrderingKind order) => order switch
    {
        SeriesOrderingKind.Publication => "publication",
        SeriesOrderingKind.Reading => "reading",
        SeriesOrderingKind.Chronological => "chronological",
        _ => "order not stated"
    };

    private static string ClassName(CatalogWorkClass workClass) => workClass switch
    {
        CatalogWorkClass.Novel => "novel",
        CatalogWorkClass.Collection => "collection",
        CatalogWorkClass.Abridgement => "abridgement",
        CatalogWorkClass.Adaptation => "adaptation",
        CatalogWorkClass.GraphicAdaptation => "graphic adaptation",
        CatalogWorkClass.StudyGuide => "study guide",
        _ => "kind not stated"
    };
}
