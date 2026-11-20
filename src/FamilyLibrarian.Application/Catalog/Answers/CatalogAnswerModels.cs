using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Answers;

/// <summary>The logical questions the shared answer service resolves. They are answers over shared reads, not new provider APIs.</summary>
public enum CatalogAnswerOperation
{
    /// <summary>The most recent original publication in the selected series, by evidenced date, never by highest number or newest edition.</summary>
    LatestPublished = 1,

    /// <summary>The entry that actually claims a stated position in the selected ordering.</summary>
    Position = 2,

    /// <summary>The next strictly greater known position after an anchor in the selected ordering.</summary>
    AfterAnchor = 3,

    /// <summary>Future-dated Works, with announced-but-undated books kept apart.</summary>
    UpcomingReleases = 4,

    /// <summary>The earliest known future-dated Work, with ties and overlapping intervals kept.</summary>
    NextRelease = 5,

    /// <summary>What sources say about when one named title was published. Each source's record keeps its own attribution.</summary>
    PublicationForTitle = 6,

    /// <summary>Known works of a selected credited author. Never a claim of an exhaustive bibliography.</summary>
    AuthorWorks = 7,

    /// <summary>Series reached from a selected author, each edge native or derived.</summary>
    AuthorSeries = 8,

    /// <summary>
    /// The known members of the selected series in one stated ordering, listed rather than selected. Always a qualified list: a read
    /// page is never the whole series, and records the source classifies as a collection, abridgement, adaptation or study guide, or
    /// that state no numeric place in this ordering, are listed apart instead of being padded into the sequence.
    /// </summary>
    SeriesMembers = 9
}

/// <summary>
/// What kind of answer this is. The outcomes are deliberately distinct: "nothing in the checked scope" is not "failed", and
/// neither is a global absence claim.
/// </summary>
public enum CatalogAnswerOutcome
{
    /// <summary>Evidenced within the stated checked scope, with no qualification that changes the result.</summary>
    Answered = 1,

    /// <summary>Known results with qualifications that must be read with them (incomplete coverage, unverified date basis, ties, ...).</summary>
    Qualified = 2,

    /// <summary>Several subjects, scopes or orderings fit and none was selected. Entries/choices are the options; nothing dependent was fetched.</summary>
    Clarification = 3,

    /// <summary>The stated, fully read checked scope held no match. Never a statement about what exists elsewhere.</summary>
    NoVerifiedMatch = 4,

    /// <summary>Not enough, or conflicting, evidence to answer; the known entries are shown with the reason.</summary>
    InsufficientEvidence = 5,

    /// <summary>No source implements what is needed. No provider call was made.</summary>
    Unsupported = 6,

    /// <summary>The source is switched off, has no credential, or refused authorization. Not a capability statement.</summary>
    Unavailable = 7,

    Throttled = 8,

    /// <summary>The deadline passed. Anything observed so far is kept; this is never read as empty.</summary>
    TimedOut = 9,

    Failed = 10,

    /// <summary>The retained context ended, was evicted, or is not this caller's. The user restarts; nothing is silently refetched.</summary>
    ContextExpired = 11,

    /// <summary>Per-user admission refused the lookup (rate limit or one already running). No provider call was made.</summary>
    Refused = 12
}

public enum CatalogSubjectKind
{
    Series = 1,
    Author = 2,
    Book = 3,
    Title = 4
}

/// <summary>
/// A selected subject: either a saved catalog id or a retained native source reference, never a name. A label is display text only.
/// </summary>
public sealed record CatalogSubjectRef
{
    public const int MaxLabelLength = 200;

    private CatalogSubjectRef(CatalogSubjectKind kind, string label, Guid? savedId, SourceRef? native, SeriesOrderingKind order)
    {
        Kind = kind;
        Label = label;
        SavedId = savedId;
        Native = native;
        Order = order;
    }

    public CatalogSubjectKind Kind { get; }

    /// <summary>Display text exactly as known; never used to select or merge.</summary>
    public string Label { get; }

    public Guid? SavedId { get; }

    public SourceRef? Native { get; }

    /// <summary>The ordering the selection was made in. <see cref="SeriesOrderingKind.Unspecified"/> is never read as publication order.</summary>
    public SeriesOrderingKind Order { get; }

    public CatalogSubjectRef WithOrder(SeriesOrderingKind order) => new(Kind, Label, SavedId, Native, order);

    public static CatalogSubjectRef Saved(CatalogSubjectKind kind, string label, Guid id, SeriesOrderingKind order = SeriesOrderingKind.Unspecified)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A saved reference needs an id.", nameof(id));
        }

        return new(kind, CleanLabel(label), id, null, order);
    }

    /// <summary>A named title being asked about. It has no identity: it can only select a lookup of records, never one record.</summary>
    public static CatalogSubjectRef Title(string label) => new(CatalogSubjectKind.Title, CleanLabel(label), null, null, SeriesOrderingKind.Unspecified);

    public static CatalogSubjectRef FromSource(CatalogSubjectKind kind, string label, SourceRef native, SeriesOrderingKind order = SeriesOrderingKind.Unspecified)
    {
        ArgumentNullException.ThrowIfNull(native);
        var expected = kind switch
        {
            CatalogSubjectKind.Series => native.Kind == ExternalReferenceEntityType.Series,
            CatalogSubjectKind.Author => native.Kind == ExternalReferenceEntityType.Author,
            CatalogSubjectKind.Book => native.Kind is ExternalReferenceEntityType.Work or ExternalReferenceEntityType.Edition,
            _ => false
        };
        if (!expected)
        {
            throw new ArgumentException("The source reference kind does not match the subject kind.", nameof(native));
        }

        return new(kind, CleanLabel(label), null, native, order);
    }

    private static string CleanLabel(string label)
    {
        // A label longer than the bound is refused, never cut: a shortened name would read as the real one.
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var trimmed = label.Trim();
        return trimmed.Length <= MaxLabelLength ? trimmed : throw new ArgumentException("The label is too long to keep whole.", nameof(label));
    }
}

/// <summary>
/// What a text can honestly say about the kind of book. Unknown stays unknown: it is never promoted to a novel or main entry,
/// and a source that assessed nothing says nothing.
/// </summary>
public enum CatalogWorkClass
{
    Unknown = 0,
    Novel = 1,
    Collection = 2,
    Abridgement = 3,
    Adaptation = 4,
    GraphicAdaptation = 5,
    StudyGuide = 6
}

/// <summary>How an entry's publication evidence relates to the pinned "today". A partial date is an interval, never an exact day.</summary>
public enum CatalogDateTemporal
{
    /// <summary>The source said nothing about a date. Not evidence there is none.</summary>
    NotStated = 0,

    /// <summary>The source lists the book as known or announced but gives no date.</summary>
    KnownUndated = 1,

    /// <summary>The whole stated interval ended before today.</summary>
    Past = 2,

    /// <summary>The whole stated interval starts after today.</summary>
    Future = 3,

    /// <summary>The stated month or year contains today: neither definitely published nor definitely upcoming.</summary>
    IncludesToday = 4
}

/// <summary>One credit as a source listed it. Exactly one of <see cref="AuthorSource"/> / <see cref="SavedAuthorId"/> may identify the person.</summary>
public sealed record CatalogEntryCredit(int Ordinal, string Label, string? Role, SourceRef? AuthorSource, Guid? SavedAuthorId)
{
    /// <summary>An absent role is "none stated", which for ordinary book credits is authorship; any other stated role is kept exactly.</summary>
    public bool IsAuthorRole => string.IsNullOrWhiteSpace(Role) || string.Equals(Role.Trim(), "author", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One book as an answer holds it: its own identity (a saved Work or a source Work/Edition), the title as reported, the position
/// claims it carries, and the publication evidence exactly as the source stated it. Nothing is merged with another entry.
/// </summary>
public sealed record CatalogAnswerEntry
{
    public Guid? SavedWorkId { get; init; }

    public SourceRef? Source { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<CatalogEntryCredit> Credits { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<CatalogEntryCredit>([]);

    /// <summary>Every position this entry claims, each with its own ordering when the source stated one. Ties and conflicts are kept.</summary>
    public IReadOnlyList<PositionClaim> Positions { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<PositionClaim>([]);

    public MembershipRole Role { get; init; } = MembershipRole.Unknown;

    public CatalogWorkClass Class { get; init; } = CatalogWorkClass.Unknown;

    /// <summary>The language the source stated; null when it stated none (never guessed).</summary>
    public string? Language { get; init; }

    public string? Format { get; init; }

    public PublicationEvidence Publication { get; init; } = PublicationEvidence.NotStated;

    /// <summary>The saved value had no recorded precision (a legacy row), so its widest reading (the year) is used. Said, never hidden.</summary>
    public bool LegacyPrecision { get; init; }

    /// <summary>Which source stated this record; null for a saved catalog row.</summary>
    public string? ProviderId { get; init; }

    public DateTimeOffset? ObservedAtUtc { get; init; }

    public string? SeriesLabel { get; init; }

    /// <summary>The stable identity key within one answer: the saved Work id or the source reference. A title is never part of it.</summary>
    public string IdentityKey => SavedWorkId is { } id ? KeyOf(id) : Source is { } source ? KeyOf(source) : "none";

    public static string KeyOf(Guid savedWorkId) => "work:" + savedWorkId.ToString("N");

    public static string KeyOf(SourceRef source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return $"src:{source.ProviderId}|{source.Kind}|{source.ExternalId}";
    }
}

/// <summary>A series an author-series answer lists, with whether the author-to-series edge is native or derived.</summary>
public sealed record CatalogAnswerSeries(
    string Label,
    SourceRef? Id,
    Guid? SavedId,
    RelationshipEvidence Relationship,
    bool? IsCompleted,
    int? PrimaryBooksCount,
    string? CanonicalState)
{
    public bool IsBound => Id is not null || SavedId is not null;
}

/// <summary>One option offered when selection needs the user: an author or series with the evidence needed to tell them apart.</summary>
public sealed record CatalogAnswerChoice(
    CatalogSubjectRef Reference,
    string? Disambiguation,
    int? KnownCount,
    string? ProviderId,
    string? EvidenceNote);

/// <summary>A named group of entries that is not the answer itself (undated announcements, other contributor roles, ...).</summary>
public sealed record CatalogAnswerGroup(string Code, IReadOnlyList<CatalogAnswerEntry> Entries)
{
    /// <summary>A private snapshot: the list the caller built, or a cast of what is returned, cannot change a group after it is made.</summary>
    public IReadOnlyList<CatalogAnswerEntry> Entries { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Entries);
}

public enum CatalogSourceState
{
    /// <summary>The source was queried and answered.</summary>
    Checked = 1,

    /// <summary>The saved catalog was read. Local only; says nothing about what exists elsewhere.</summary>
    SavedCatalog = 2,

    /// <summary>The integration does not implement what was needed. No call was made.</summary>
    Unsupported = 3,

    /// <summary>Disabled, removed, no credential, or authorization refused.</summary>
    Unavailable = 4,

    Throttled = 5,

    TimedOut = 6,

    Failed = 7,

    /// <summary>An active source that was not asked. Another source is an explicit later choice, never a silent fan-out.</summary>
    NotQueried = 8
}

/// <summary>What happened at one source, so an absence is always read against what was actually checked.</summary>
public sealed record CatalogSourceReport(string ProviderId, CatalogSourceState State, string? ReasonCode, DiscoveryCompletion? Completion);

/// <summary>What was checked and how far. "Exhausted" means the checked scope was read to its end, never that a bibliography is complete.</summary>
public sealed record CatalogAnswerCoverage(
    IReadOnlyList<CatalogSourceReport> Sources,
    string ScopeCode,
    bool ExhaustedInCheckedScope,
    bool MoreAvailable,
    bool Truncated,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>A private snapshot of the source reports (see <see cref="CatalogAnswerGroup.Entries"/>).</summary>
    public IReadOnlyList<CatalogSourceReport> Sources { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Sources);

    public static CatalogAnswerCoverage None(DateTimeOffset at) => new([], "none", false, false, false, at);

    /// <summary>True when every consulted source answered; a source that failed, was unavailable, unsupported or not queried is a gap.</summary>
    public bool HasSourceGaps => Sources.Any(source => source.State is not (CatalogSourceState.Checked or CatalogSourceState.SavedCatalog));
}

/// <summary>Physical use, counted from the journey budget. Rendering, cached reads and retained-context follow-ups cost nothing here.</summary>
public sealed record CatalogAnswerUsage(int ProviderReads, int Attempts, int Retries, int Pages, int Items, TimeSpan Elapsed)
{
    public static CatalogAnswerUsage None { get; } = new(0, 0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// The immutable result of one answer. The envelope states the operation, the subject and order it was made in, the outcome, the
/// entries that are the answer, the groups that are not, the stable qualifier and reason codes, what was checked and how much
/// it cost. It carries no model text, no provider text and no write.
/// </summary>
public sealed record CatalogAnswer
{
    public required CatalogAnswerOperation Operation { get; init; }

    public required CatalogAnswerOutcome Outcome { get; init; }

    public CatalogSubjectRef? Subject { get; init; }

    public CatalogSubjectRef? Anchor { get; init; }

    /// <summary>The ordering the answer was selected in. Unspecified is stated, never read as publication order.</summary>
    public SeriesOrderingKind Order { get; init; } = SeriesOrderingKind.Unspecified;

    /// <summary>The position the question asked for, kept so the answer can say which one it is about; null when none was asked.</summary>
    public decimal? Position { get; init; }

    /// <summary>The entries that are the answer: one, or several when ties/overlaps are kept.</summary>
    public IReadOnlyList<CatalogAnswerEntry> Entries { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<CatalogAnswerEntry>([]);

    public IReadOnlyList<CatalogAnswerSeries> Series { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<CatalogAnswerSeries>([]);

    /// <summary>The orderings the entries actually state, listed when the ordering must be chosen. Empty otherwise.</summary>
    public IReadOnlyList<SeriesOrderingKind> AvailableOrders { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<SeriesOrderingKind>([]);

    public IReadOnlyList<CatalogAnswerChoice> Choices { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<CatalogAnswerChoice>([]);

    public IReadOnlyList<CatalogAnswerGroup> Groups { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<CatalogAnswerGroup>([]);

    /// <summary>Stable codes that change how the result must be read (see <see cref="CatalogAnswerCodes"/>).</summary>
    public IReadOnlyList<string> Qualifiers { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<string>([]);

    /// <summary>Stable codes saying why the outcome is not a plain answer.</summary>
    public IReadOnlyList<string> Reasons { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of<string>([]);

    public CatalogAnswerCoverage Coverage { get; init; } = CatalogAnswerCoverage.None(DateTimeOffset.MinValue);

    public CatalogAnswerUsage Usage { get; init; } = CatalogAnswerUsage.None;

    /// <summary>The day the temporal rules were evaluated against (from the injected clock).</summary>
    public DateOnly Today { get; init; }

    /// <summary>Reference to the retained bounded context, for follow-ups; null when nothing could be retained.</summary>
    public CatalogAnswerContextRef? Context { get; init; }

    /// <summary>Server-issued continuation for one more page of the same lookup; never client-supplied.</summary>
    public string? ContinuationToken { get; init; }

    /// <summary>When the admission rule refused the lookup, how long until the current window ends. Zero otherwise.</summary>
    public TimeSpan RetryAfter { get; init; }

    public bool HasQualifier(string code) => Qualifiers.Contains(code, StringComparer.Ordinal);
}

/// <summary>Stable lower-case codes used in answer qualifiers and reasons (the shared <see cref="DiscoveryCodes"/> shape).</summary>
public static class CatalogAnswerCodes
{
    // ---- qualifiers
    public const string IncompleteCoverage = "incomplete-coverage";
    public const string KnownLocalOnly = "known-local-only";
    public const string DateBasisUnverified = "date-basis-unverified";
    public const string StatusUnverified = "status-unverified";
    public const string TiedOrOverlappingDates = "tied-or-overlapping-dates";
    public const string TiedPosition = "tied-position";
    public const string UndatedEntriesPresent = "undated-entries-present";
    public const string UnnumberedEntriesPresent = "unnumbered-entries-present";
    public const string ClassUnverified = "class-unverified";
    public const string RoleUnverified = "role-unverified";
    public const string LanguageUnverified = "language-unverified";
    public const string PositionConflict = "position-conflict";
    public const string StatusDateConflict = "status-date-conflict";
    public const string ConflictingDates = "conflicting-dates";
    public const string NextKnownNotProvenImmediate = "next-known-not-proven-immediate";
    public const string ReleaseDateAttributed = "release-date-attributed";
    public const string ListNotExhaustive = "list-not-exhaustive";
    public const string DerivedEdges = "derived-edges";
    public const string LegacyPrecision = "legacy-precision";
    public const string SourceGaps = "source-gaps";
    public const string IncludesTodayInterval = "interval-includes-today";
    public const string EditionNotOriginal = "edition-not-original";
    public const string MultipleSourceRecords = "multiple-source-records";
    public const string ClassNotOriginal = "class-not-original";
    public const string CreditUnverified = "credit-unverified";

    // ---- reasons
    public const string OrderAmbiguous = "order-ambiguous";
    public const string NamesakeChoices = "namesake-choices";
    public const string SeriesChoices = "series-choices";
    public const string AuthorChoices = "author-choices";
    public const string NoDatedPublishedEntry = "no-dated-published-entry";
    public const string NoFutureDatedEntry = "no-future-dated-entry";
    public const string PositionNotInCheckedEntries = "position-not-in-checked-entries";
    public const string AnchorNotFound = "anchor-not-found";
    public const string AnchorPositionUnknown = "anchor-position-unknown";
    public const string AnchorPositionConflict = "anchor-position-conflict";
    public const string NoLaterKnownEntry = "no-later-known-entry";
    public const string NoExactTitleMatch = "no-exact-title-match";
    public const string NoEntries = "no-entries";
    public const string SubjectRequired = "subject-required";
    public const string SubjectInvalid = "subject-invalid";
    public const string OperationNotSupported = "operation-not-supported";
    public const string NoEligibleSource = "no-eligible-source";
    public const string ContextExpiredReason = "context-expired";
    public const string ContextNotRetained = "context-not-retained";
    public const string AdmissionRefused = "admission-refused";
    public const string DeadlineReached = "deadline-reached";
    public const string SeedNoUsableRecords = "seed-no-usable-records";
    public const string ClassFilterApplied = "class-filter-applied";
    public const string AuthorIdsUnavailable = "author-ids-unavailable";
    public const string SeriesClaimsUnsupported = "series-claims-unsupported";
    public const string NoChoicesFound = "no-choices-found";
    public const string ContinuationUnavailable = "continuation-unavailable";
    public const string ContextWindowCut = "context-window-cut";
    public const string EditionTitlesUnavailable = "edition-titles-unavailable";
}
