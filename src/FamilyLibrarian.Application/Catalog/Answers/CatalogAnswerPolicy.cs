using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Domain.Catalog;
using PositionClaim = FamilyLibrarian.Application.Catalog.Discovery.PositionClaim;

namespace FamilyLibrarian.Application.Catalog.Answers;

/// <summary>Optional filters. They act only on stated evidence: an unknown role, class or language is never promoted to a match.</summary>
public sealed record CatalogAnswerFilter(bool MainOnly = false, bool NovelOnly = false, string? Language = null)
{
    public static CatalogAnswerFilter None { get; } = new();

    public bool IsActive => MainOnly || NovelOnly || !string.IsNullOrWhiteSpace(Language);
}

/// <summary>How much of the subject the entries cover. The policy never reads more into the entries than this says.</summary>
/// <param name="Exhausted">The checked scope was read to its end (never a claim that a bibliography is complete).</param>
/// <param name="SavedOnly">The entries are the saved catalog only.</param>
/// <param name="SourceGaps">A consulted source failed, was unavailable, unsupported or not queried.</param>
public sealed record CatalogPolicyScope(bool Exhausted, bool SavedOnly, bool SourceGaps);

/// <summary>What a pure selection decided; the service adds subject, coverage and usage around it.</summary>
public sealed record CatalogSelection(
    CatalogAnswerOutcome Outcome,
    IReadOnlyList<CatalogAnswerEntry> Entries,
    IReadOnlyList<CatalogAnswerGroup> Groups,
    IReadOnlyList<string> Qualifiers,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<SeriesOrderingKind> AvailableOrders)
{
    public static readonly IReadOnlyList<SeriesOrderingKind> NoOrders = [];
}

/// <summary>
/// The deterministic answer rules, as pure functions of the entries, the filter, the ordering and a pinned day. No clock, no I/O,
/// no model. Each function preserves what the entries actually state: partial dates stay intervals, distinct orderings stay
/// distinct, ties and conflicts stay visible, unknown classification stays unknown, and a result is never wider than its scope.
/// </summary>
public static class CatalogAnswerPolicy
{
    // ================================================================================================== date reasoning

    public static CatalogDateTemporal TemporalOf(PublicationEvidence publication, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(publication);
        switch (publication.State)
        {
            case PublicationDateState.NotStated:
                return CatalogDateTemporal.NotStated;
            case PublicationDateState.KnownUndated:
                return CatalogDateTemporal.KnownUndated;
        }

        var date = publication.Date!.Value;
        var precision = publication.Precision ?? DatePrecision.Day;
        if (precision == DatePrecision.Day)
        {
            // A full date that is today has happened; one after today has not.
            return date <= today ? CatalogDateTemporal.Past : CatalogDateTemporal.Future;
        }

        var (first, last) = PublicationStatusPolicy.Interval(date, precision);
        if (last < today)
        {
            return CatalogDateTemporal.Past;
        }

        return first > today ? CatalogDateTemporal.Future : CatalogDateTemporal.IncludesToday;
    }

    /// <summary>The days the stated date could mean. Only meaningful for a dated entry.</summary>
    public static (DateOnly First, DateOnly Last) IntervalOf(PublicationEvidence publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return PublicationStatusPolicy.Interval(publication.Date!.Value, publication.Precision ?? DatePrecision.Day);
    }

    /// <summary>A is strictly after B only when A's whole interval starts after B's ends. Overlapping or nested intervals are not ordered.</summary>
    public static bool StrictlyAfter(CatalogAnswerEntry a, CatalogAnswerEntry b) => IntervalOf(a.Publication).First > IntervalOf(b.Publication).Last;

    // =================================================================================================== latest published

    public static CatalogSelection Latest(
        IReadOnlyList<CatalogAnswerEntry> entries, DateOnly today, CatalogPolicyScope scope, CatalogAnswerFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        var kept = ApplyFilter(entries, filter ?? CatalogAnswerFilter.None, state);

        var ranked = new List<CatalogAnswerEntry>();
        var editionDerived = new List<CatalogAnswerEntry>();
        var nonOriginalClass = new List<CatalogAnswerEntry>();
        var undated = new List<CatalogAnswerEntry>();
        var future = new List<CatalogAnswerEntry>();
        var includesToday = new List<CatalogAnswerEntry>();
        foreach (var entry in kept)
        {
            if (IsNotOriginalClass(entry.Class))
            {
                nonOriginalClass.Add(entry);
                continue;
            }

            switch (TemporalOf(entry.Publication, today))
            {
                case CatalogDateTemporal.Past when entry.Publication.Basis == DateBasis.EditionDerived:
                    // An edition date can be a reprint's: it is shown, never ranked as the original publication.
                    editionDerived.Add(entry);
                    break;
                case CatalogDateTemporal.Past:
                    ranked.Add(entry);
                    break;
                case CatalogDateTemporal.Future:
                    future.Add(entry);
                    break;
                case CatalogDateTemporal.IncludesToday:
                    includesToday.Add(entry);
                    break;
                default:
                    undated.Add(entry);
                    break;
            }
        }

        state.Group("excluded-by-class", nonOriginalClass);
        state.Group("edition-derived-dates", editionDerived);
        state.Group("future-dated", future);
        state.Group("undated", undated);
        state.Group("interval-includes-today", includesToday);
        if (undated.Count > 0) state.Qualify(CatalogAnswerCodes.UndatedEntriesPresent);
        if (includesToday.Count > 0) state.Qualify(CatalogAnswerCodes.IncludesTodayInterval);
        if (nonOriginalClass.Count > 0) state.Reason(CatalogAnswerCodes.ClassFilterApplied);

        if (ranked.Count == 0)
        {
            if (kept.Count == 0 && state.Groups.Count == 0 && entries.Count == 0)
            {
                return NothingFound(state, scope, CatalogAnswerCodes.NoEntries);
            }

            state.Reason(CatalogAnswerCodes.NoDatedPublishedEntry);
            state.Coverage(scope);
            return state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        // The latest are the entries no other ranked entry is strictly after. Several remain when intervals tie or overlap.
        var maximal = ranked
            .Where(candidate => !ranked.Any(other => StrictlyAfter(other, candidate)))
            .OrderByDescending(entry => IntervalOf(entry.Publication).Last)
            .ThenBy(entry => entry.IdentityKey, StringComparer.Ordinal)
            .ToArray();

        // A work known only by an edition date was first published no later than that date. It can therefore be newer than the
        // selected entry only when its edition date reaches past the selected one; an older edition date proves it is not.
        var newestSelected = maximal.Max(entry => IntervalOf(entry.Publication).Last);
        if (editionDerived.Any(entry => IntervalOf(entry.Publication).Last > newestSelected)) state.Qualify(CatalogAnswerCodes.EditionNotOriginal);
        if (maximal.Length > 1) state.Qualify(CatalogAnswerCodes.TiedOrOverlappingDates);
        if (maximal.Any(entry => entry.Publication.Basis == DateBasis.Unknown)) state.Qualify(CatalogAnswerCodes.DateBasisUnverified);
        if (maximal.Any(entry => entry.LegacyPrecision)) state.Qualify(CatalogAnswerCodes.LegacyPrecision);
        state.Coverage(scope);
        return state.Finish(state.Qualifiers.Count == 0 ? CatalogAnswerOutcome.Answered : CatalogAnswerOutcome.Qualified, maximal);
    }

    // ======================================================================================================== book N

    public static CatalogSelection Position(
        IReadOnlyList<CatalogAnswerEntry> entries,
        decimal position,
        SeriesOrderingKind order,
        CatalogPolicyScope scope,
        CatalogAnswerFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        var kept = ApplyFilter(entries, filter ?? CatalogAnswerFilter.None, state);

        var orders = OrdersOf(kept);
        if (order == SeriesOrderingKind.Unspecified && orders.Count > 1)
        {
            // Publication, reading and chronological numbers are different facts: asking for "book N" without choosing one mixes them.
            state.Reason(CatalogAnswerCodes.OrderAmbiguous);
            return state.Finish(CatalogAnswerOutcome.Clarification, [], orders);
        }

        var matches = kept
            .Where(entry => entry.Positions.Any(claim => claim.Sort == position && OrderMatches(claim, order)))
            .OrderBy(entry => entry.IdentityKey, StringComparer.Ordinal)
            .ToArray();
        state.Group("ordering-unstated", order == SeriesOrderingKind.Unspecified
            ? []
            : kept.Where(entry => entry.Positions.Any(claim => claim.Sort == position && claim.Ordering is null)).Except(matches).ToArray());

        if (matches.Length == 0)
        {
            state.Reason(CatalogAnswerCodes.PositionNotInCheckedEntries);
            state.Coverage(scope);
            return scope.Exhausted && !scope.SourceGaps
                ? state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, [])
                : state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        if (matches.Length > 1) state.Qualify(CatalogAnswerCodes.TiedPosition);
        if (matches.Any(entry => ClaimedSorts(entry, order).Length > 1)) state.Qualify(CatalogAnswerCodes.PositionConflict);
        if (matches.Any(entry => IsNotOriginalClass(entry.Class))) state.Qualify(CatalogAnswerCodes.ClassNotOriginal);
        // A claimed position is a fact whatever the coverage; only a source gap or local-only knowledge qualifies it.
        if (scope.SourceGaps) state.Qualify(CatalogAnswerCodes.SourceGaps);
        if (scope.SavedOnly) state.Qualify(CatalogAnswerCodes.KnownLocalOnly);
        return state.Finish(state.Qualifiers.Count == 0 ? CatalogAnswerOutcome.Answered : CatalogAnswerOutcome.Qualified, matches);
    }

    // =================================================================================================== next in order

    /// <param name="anchorIdentityKey">The anchor entry's <see cref="CatalogAnswerEntry.IdentityKey"/>; null when anchoring by position.</param>
    /// <param name="anchorPosition">A stated position to anchor on when no entry identity is held.</param>
    public static CatalogSelection AfterAnchor(
        IReadOnlyList<CatalogAnswerEntry> entries,
        string? anchorIdentityKey,
        decimal? anchorPosition,
        SeriesOrderingKind order,
        CatalogPolicyScope scope,
        CatalogAnswerFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        var kept = ApplyFilter(entries, filter ?? CatalogAnswerFilter.None, state);

        var orders = OrdersOf(kept);
        if (order == SeriesOrderingKind.Unspecified && orders.Count > 1)
        {
            state.Reason(CatalogAnswerCodes.OrderAmbiguous);
            return state.Finish(CatalogAnswerOutcome.Clarification, [], orders);
        }

        decimal baseline;
        CatalogAnswerEntry[] anchors;
        if (anchorIdentityKey is not null)
        {
            anchors = entries.Where(entry => string.Equals(entry.IdentityKey, anchorIdentityKey, StringComparison.Ordinal)).ToArray();
            if (anchors.Length == 0)
            {
                state.Reason(CatalogAnswerCodes.AnchorNotFound);
                return state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
            }

            var sorts = ClaimedSorts(anchors[0], order);
            if (sorts.Length == 0)
            {
                // The anchor is known but its place is not: the user is asked rather than a position guessed.
                state.Reason(CatalogAnswerCodes.AnchorPositionUnknown);
                return state.Finish(CatalogAnswerOutcome.Clarification, anchors);
            }

            if (sorts.Length > 1)
            {
                state.Reason(CatalogAnswerCodes.AnchorPositionConflict);
                return state.Finish(CatalogAnswerOutcome.InsufficientEvidence, anchors);
            }

            baseline = sorts[0];
        }
        else if (anchorPosition is { } stated)
        {
            baseline = stated;
            anchors = kept.Where(entry => ClaimedSorts(entry, order).Contains(stated)).ToArray();
        }
        else
        {
            state.Reason(CatalogAnswerCodes.AnchorNotFound);
            return state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        var anchorKeys = anchors.Select(entry => entry.IdentityKey).ToHashSet(StringComparer.Ordinal);
        var later = kept
            .Where(entry => !anchorKeys.Contains(entry.IdentityKey))
            .Select(entry => (Entry: entry, Next: ClaimedSorts(entry, order).Where(sort => sort > baseline).OrderBy(sort => sort).Cast<decimal?>().FirstOrDefault()))
            .Where(pair => pair.Next is not null)
            .ToArray();
        state.Group("unnumbered", kept.Where(entry => !anchorKeys.Contains(entry.IdentityKey) && ClaimedSorts(entry, order).Length == 0).ToArray());

        if (later.Length == 0)
        {
            state.Reason(CatalogAnswerCodes.NoLaterKnownEntry);
            state.Coverage(scope);
            return scope.Exhausted && !scope.SourceGaps && state.Groups.Count == 0
                ? state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, [])
                : state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        var nextSort = later.Min(pair => pair.Next!.Value);
        var selected = later
            .Where(pair => pair.Next == nextSort)
            .Select(pair => pair.Entry)
            .OrderBy(entry => entry.IdentityKey, StringComparer.Ordinal)
            .ToArray();

        if (selected.Length > 1) state.Qualify(CatalogAnswerCodes.TiedPosition);
        if (state.HasGroup("unnumbered")) state.Qualify(CatalogAnswerCodes.UnnumberedEntriesPresent);
        if (selected.Any(entry => IsNotOriginalClass(entry.Class))) state.Qualify(CatalogAnswerCodes.ClassNotOriginal);
        if (!scope.Exhausted) state.Qualify(CatalogAnswerCodes.NextKnownNotProvenImmediate);
        state.Coverage(scope);
        return state.Finish(state.Qualifiers.Count == 0 ? CatalogAnswerOutcome.Answered : CatalogAnswerOutcome.Qualified, selected);
    }

    // ==================================================================================================== upcoming / next

    public static CatalogSelection Upcoming(
        IReadOnlyList<CatalogAnswerEntry> entries,
        DateOnly today,
        bool nextOnly,
        CatalogPolicyScope scope,
        CatalogAnswerFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        var kept = ApplyFilter(entries, filter ?? CatalogAnswerFilter.None, state);

        var works = new List<CatalogAnswerEntry>();
        var futureEditions = new List<CatalogAnswerEntry>();
        var possibly = new List<CatalogAnswerEntry>();
        var announcedUndated = new List<CatalogAnswerEntry>();
        var notStated = 0;
        foreach (var entry in kept)
        {
            switch (TemporalOf(entry.Publication, today))
            {
                case CatalogDateTemporal.Future when IsWorkLevelDate(entry):
                    works.Add(entry);
                    break;
                case CatalogDateTemporal.Future:
                    // A future edition or reprint stays one: it is not a new original work.
                    futureEditions.Add(entry);
                    break;
                case CatalogDateTemporal.IncludesToday:
                    possibly.Add(entry);
                    break;
                case CatalogDateTemporal.KnownUndated:
                    announcedUndated.Add(entry);
                    break;
                case CatalogDateTemporal.NotStated:
                    notStated++;
                    break;
            }
        }

        state.Group("future-editions", futureEditions);
        state.Group("possibly-upcoming", possibly);
        state.Group("announced-undated", announcedUndated);
        if (futureEditions.Count > 0) state.Qualify(CatalogAnswerCodes.EditionNotOriginal);
        if (possibly.Count > 0) state.Qualify(CatalogAnswerCodes.IncludesTodayInterval);
        if (notStated > 0) state.Qualify(CatalogAnswerCodes.UndatedEntriesPresent);

        var ordered = works
            .OrderBy(entry => IntervalOf(entry.Publication).First)
            .ThenBy(entry => entry.IdentityKey, StringComparer.Ordinal)
            .ToArray();
        var selected = nextOnly
            ? ordered.Where(candidate => !ordered.Any(other => StrictlyAfter(candidate, other))).ToArray()
            : ordered;

        if (selected.Length == 0)
        {
            state.Reason(CatalogAnswerCodes.NoFutureDatedEntry);
            state.Coverage(scope);
            if (announcedUndated.Count > 0)
            {
                // Announced but undated is a known fact about the series, not an absence.
                return state.Finish(CatalogAnswerOutcome.Qualified, []);
            }

            return scope.Exhausted && !scope.SourceGaps && state.Qualifiers.Count == 0
                ? state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, [])
                : state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        if (nextOnly && selected.Length > 1) state.Qualify(CatalogAnswerCodes.TiedOrOverlappingDates);
        if (selected.Any(entry => entry.Publication.Basis == DateBasis.Unknown))
        {
            // A source's future date is an attributed claim until its meaning is verified.
            state.Qualify(CatalogAnswerCodes.DateBasisUnverified);
            state.Qualify(CatalogAnswerCodes.ReleaseDateAttributed);
        }

        // "Scheduled" is never inferred from a date: only a source's own forthcoming statement counts as that evidence.
        if (selected.Any(entry => entry.Publication.SourceStatus == PublicationStatus.Unknown)) state.Qualify(CatalogAnswerCodes.StatusUnverified);
        if (selected.Any(entry => entry.Publication.SourceStatus == PublicationStatus.Published)) state.Qualify(CatalogAnswerCodes.StatusDateConflict);
        if (selected.Any(entry => entry.LegacyPrecision)) state.Qualify(CatalogAnswerCodes.LegacyPrecision);
        if (!nextOnly && !scope.Exhausted) state.Qualify(CatalogAnswerCodes.ListNotExhaustive);
        state.Coverage(scope);
        return state.Finish(state.Qualifiers.Count == 0 ? CatalogAnswerOutcome.Answered : CatalogAnswerOutcome.Qualified, selected);
    }

    // ===================================================================================================== named title

    public static CatalogSelection PublicationForTitle(
        IReadOnlyList<CatalogAnswerEntry> entries, string title, DateOnly today, CatalogPolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        var key = CatalogIdentityPolicy.TitleKey(title);
        var matches = entries
            .Where(entry => string.Equals(CatalogIdentityPolicy.TitleKey(entry.Title), key, StringComparison.Ordinal))
            .OrderBy(entry => entry.ProviderId, StringComparer.Ordinal)
            .ThenBy(entry => entry.IdentityKey, StringComparer.Ordinal)
            .ToArray();

        if (matches.Length == 0)
        {
            // Keyword hits are never relabelled as the answer: records that merely resemble the title are not listed.
            state.Reason(CatalogAnswerCodes.NoExactTitleMatch);
            state.Qualify(CatalogAnswerCodes.IncompleteCoverage);
            if (scope.SourceGaps) state.Qualify(CatalogAnswerCodes.SourceGaps);
            return state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, []);
        }

        // Two different records of one source under one title are namesakes: the user chooses, nothing is merged.
        if (matches.GroupBy(entry => entry.ProviderId, StringComparer.Ordinal).Any(group => group.Select(entry => entry.IdentityKey).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            state.Reason(CatalogAnswerCodes.NamesakeChoices);
            return state.Finish(CatalogAnswerOutcome.Clarification, matches);
        }

        if (matches.Length > 1) state.Qualify(CatalogAnswerCodes.MultipleSourceRecords);
        var dated = matches.Where(entry => entry.Publication.State == PublicationDateState.Dated).ToArray();
        if (dated.Length > 1 && dated.Any(left => dated.Any(right => StrictlyAfter(left, right)))) state.Qualify(CatalogAnswerCodes.ConflictingDates);
        if (dated.Any(entry => entry.Publication.Basis == DateBasis.Unknown)) state.Qualify(CatalogAnswerCodes.DateBasisUnverified);
        if (dated.Any(entry => entry.Publication.Basis == DateBasis.EditionDerived)) state.Qualify(CatalogAnswerCodes.EditionNotOriginal);
        if (matches.Any(entry => entry.Publication.State != PublicationDateState.Dated)) state.Qualify(CatalogAnswerCodes.UndatedEntriesPresent);
        if (dated.Any(entry => TemporalOf(entry.Publication, today) == CatalogDateTemporal.IncludesToday)) state.Qualify(CatalogAnswerCodes.IncludesTodayInterval);
        if (dated.Any(entry => entry.Publication.SourceStatus == PublicationStatus.Published && TemporalOf(entry.Publication, today) == CatalogDateTemporal.Future))
        {
            state.Qualify(CatalogAnswerCodes.StatusDateConflict);
        }

        if (dated.Any(entry => entry.LegacyPrecision)) state.Qualify(CatalogAnswerCodes.LegacyPrecision);
        if (scope.SourceGaps) state.Qualify(CatalogAnswerCodes.SourceGaps);
        return state.Finish(state.Qualifiers.Count == 0 ? CatalogAnswerOutcome.Answered : CatalogAnswerOutcome.Qualified, matches);
    }

    // ======================================================================================================== authors

    public static CatalogSelection AuthorWorks(
        IReadOnlyList<CatalogAnswerEntry> entries, CatalogSubjectRef author, CatalogPolicyScope scope)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        var credited = new List<CatalogAnswerEntry>();
        var otherRoles = new List<CatalogAnswerEntry>();
        var unverified = new List<CatalogAnswerEntry>();
        foreach (var entry in entries)
        {
            var mine = entry.Credits.Where(credit => IsSameAuthor(credit, author)).ToArray();
            if (mine.Any(credit => credit.IsAuthorRole))
            {
                credited.Add(entry);
            }
            else if (mine.Length > 0)
            {
                // An editor, translator or illustrator: kept with the role stated, never counted as the author.
                otherRoles.Add(entry);
            }
            else
            {
                // The source returned this book for the author but listed no credit that identifies them: unverified, not credited.
                unverified.Add(entry);
            }
        }

        state.Group("other-roles", otherRoles);
        state.Group("credit-unverified", unverified);
        if (unverified.Count > 0) state.Qualify(CatalogAnswerCodes.CreditUnverified);

        var selected = credited
            .OrderBy(entry => CatalogIdentityPolicy.TitleKey(entry.Title), StringComparer.Ordinal)
            .ThenBy(entry => entry.IdentityKey, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0)
        {
            state.Reason(CatalogAnswerCodes.NoEntries);
            state.Coverage(scope);
            return scope.Exhausted && !scope.SourceGaps && state.Groups.Count == 0
                ? state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, [])
                : state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        // A page of known works is never a bibliography: this stays a qualified answer even when the checked scope was read to its end.
        state.Qualify(CatalogAnswerCodes.ListNotExhaustive);
        state.Coverage(scope);
        return state.Finish(CatalogAnswerOutcome.Qualified, selected);
    }

    public static CatalogSelection AuthorSeries(IReadOnlyList<CatalogAnswerSeries> series, CatalogPolicyScope scope, out IReadOnlyList<CatalogAnswerSeries> ordered)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(scope);
        var state = new Builder();
        ordered = series
            .OrderBy(item => CatalogIdentityPolicy.TitleKey(item.Label), StringComparer.Ordinal)
            .ThenBy(item => item.Id?.ExternalId ?? item.SavedId?.ToString("N") ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Count == 0)
        {
            state.Reason(CatalogAnswerCodes.NoEntries);
            state.Coverage(scope);
            return scope.Exhausted && !scope.SourceGaps
                ? state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, [])
                : state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
        }

        if (ordered.Any(item => item.Relationship.Basis == RelationshipBasis.Derived)) state.Qualify(CatalogAnswerCodes.DerivedEdges);
        state.Qualify(CatalogAnswerCodes.ListNotExhaustive);
        state.Coverage(scope);
        return state.Finish(CatalogAnswerOutcome.Qualified, []);
    }

    // ================================================================================================ shared helpers

    public static bool IsNotOriginalClass(CatalogWorkClass workClass) =>
        workClass is CatalogWorkClass.Collection or CatalogWorkClass.Abridgement or CatalogWorkClass.Adaptation
            or CatalogWorkClass.GraphicAdaptation or CatalogWorkClass.StudyGuide;

    /// <summary>A date that is a Work's own (a saved Work, or a source Work whose date is not stated to be one edition's).</summary>
    private static bool IsWorkLevelDate(CatalogAnswerEntry entry) =>
        entry.Publication.Basis != DateBasis.EditionDerived &&
        (entry.SavedWorkId is not null || entry.Source?.Kind == ExternalReferenceEntityType.Work);

    private static bool IsSameAuthor(CatalogEntryCredit credit, CatalogSubjectRef author) =>
        (author.SavedId is { } saved && credit.SavedAuthorId == saved) ||
        (author.Native is { } native && credit.AuthorSource == native);

    private static bool OrderMatches(PositionClaim claim, SeriesOrderingKind order) =>
        order == SeriesOrderingKind.Unspecified || claim.Ordering == order;

    /// <summary>Distinct numeric positions the entry claims in the selected order, ascending.</summary>
    private static decimal[] ClaimedSorts(CatalogAnswerEntry entry, SeriesOrderingKind order) =>
        entry.Positions
            .Where(claim => claim.Sort is not null && OrderMatches(claim, order))
            .Select(claim => claim.Sort!.Value)
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>The distinct orderings the entries actually state. An unstated ordering is not a kind and is never counted as one.</summary>
    public static IReadOnlyList<SeriesOrderingKind> OrdersOf(IEnumerable<CatalogAnswerEntry> entries) =>
        entries
            .SelectMany(entry => entry.Positions)
            .Select(claim => claim.Ordering)
            .Where(kind => kind is not null and not SeriesOrderingKind.Unspecified)
            .Select(kind => kind!.Value)
            .Distinct()
            .Order()
            .ToArray();

    private static IReadOnlyList<CatalogAnswerEntry> ApplyFilter(
        IReadOnlyList<CatalogAnswerEntry> entries, CatalogAnswerFilter filter, Builder state)
    {
        if (!filter.IsActive) return entries;
        var kept = new List<CatalogAnswerEntry>();
        var roleUnknown = new List<CatalogAnswerEntry>();
        var classUnknown = new List<CatalogAnswerEntry>();
        var languageUnknown = new List<CatalogAnswerEntry>();
        var excluded = new List<CatalogAnswerEntry>();
        foreach (var entry in entries)
        {
            if (filter.MainOnly)
            {
                if (entry.Role == MembershipRole.Unknown) { roleUnknown.Add(entry); continue; }
                if (entry.Role != MembershipRole.Main) { excluded.Add(entry); continue; }
            }

            if (filter.NovelOnly)
            {
                if (entry.Class == CatalogWorkClass.Unknown) { classUnknown.Add(entry); continue; }
                if (entry.Class != CatalogWorkClass.Novel) { excluded.Add(entry); continue; }
            }

            if (!string.IsNullOrWhiteSpace(filter.Language))
            {
                if (string.IsNullOrWhiteSpace(entry.Language)) { languageUnknown.Add(entry); continue; }
                if (!string.Equals(entry.Language.Trim(), filter.Language.Trim(), StringComparison.OrdinalIgnoreCase)) { excluded.Add(entry); continue; }
            }

            kept.Add(entry);
        }

        // An unverified entry is neither a match nor a non-match: it is listed apart, and the answer says it could not be verified.
        state.Group("role-unverified", roleUnknown);
        state.Group("class-unverified", classUnknown);
        state.Group("language-unverified", languageUnknown);
        state.Group("excluded-by-filter", excluded);
        if (roleUnknown.Count > 0) state.Qualify(CatalogAnswerCodes.RoleUnverified);
        if (classUnknown.Count > 0) state.Qualify(CatalogAnswerCodes.ClassUnverified);
        if (languageUnknown.Count > 0) state.Qualify(CatalogAnswerCodes.LanguageUnverified);
        return kept;
    }

    private static CatalogSelection NothingFound(Builder state, CatalogPolicyScope scope, string reason)
    {
        state.Reason(reason);
        state.Coverage(scope);
        return scope.Exhausted && !scope.SourceGaps
            ? state.Finish(CatalogAnswerOutcome.NoVerifiedMatch, [])
            : state.Finish(CatalogAnswerOutcome.InsufficientEvidence, []);
    }

    private sealed class Builder
    {
        private readonly List<string> qualifiers = [];
        private readonly List<string> reasons = [];
        private readonly List<CatalogAnswerGroup> groups = [];

        public List<string> Qualifiers => qualifiers;

        public List<CatalogAnswerGroup> Groups => groups;

        public bool HasGroup(string code) => groups.Any(group => group.Code == code);

        public void Qualify(string code)
        {
            if (!qualifiers.Contains(code, StringComparer.Ordinal)) qualifiers.Add(code);
        }

        public void Reason(string code)
        {
            if (!reasons.Contains(code, StringComparer.Ordinal)) reasons.Add(code);
        }

        public void Group(string code, IReadOnlyList<CatalogAnswerEntry> entries)
        {
            if (entries.Count > 0) groups.Add(new CatalogAnswerGroup(code, entries.ToArray()));
        }

        /// <summary>Coverage limits restate what the entries cannot prove: an unread remainder or a source gap blocks a plain answer.</summary>
        public void Coverage(CatalogPolicyScope scope)
        {
            // The saved catalog is only what this library already knows: whatever it answers, it answers as local knowledge.
            if (scope.SavedOnly) Qualify(CatalogAnswerCodes.KnownLocalOnly);
            if (!scope.Exhausted) Qualify(CatalogAnswerCodes.IncompleteCoverage);

            if (scope.SourceGaps) Qualify(CatalogAnswerCodes.SourceGaps);
        }

        public CatalogSelection Finish(
            CatalogAnswerOutcome outcome, IReadOnlyList<CatalogAnswerEntry> entries, IReadOnlyList<SeriesOrderingKind>? orders = null) =>
            new(outcome, entries.ToArray(), groups.ToArray(), qualifiers.ToArray(), reasons.ToArray(), orders ?? CatalogSelection.NoOrders);
    }
}
