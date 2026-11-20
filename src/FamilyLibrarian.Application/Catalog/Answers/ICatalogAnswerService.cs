using FamilyLibrarian.Application.Catalog.Discovery;

namespace FamilyLibrarian.Application.Catalog.Answers;

/// <summary>
/// One question put to the shared answer service. Selection is by validated references (a saved id or a retained native source
/// reference), never by a name: a name only ever produces choices.
/// </summary>
public sealed record CatalogAnswerRequest
{
    public required CatalogAnswerOperation Operation { get; init; }

    /// <summary>The selected series (series operations) or author (author operations). Null asks the service to offer choices for <see cref="Text"/>.</summary>
    public CatalogSubjectRef? Subject { get; init; }

    /// <summary>The name or title typed, used only to find choices (or, for a title lookup, the title to check). Never identity.</summary>
    public string? Text { get; init; }

    /// <summary>The position asked for (<see cref="CatalogAnswerOperation.Position"/>), or an anchor position for <see cref="CatalogAnswerOperation.AfterAnchor"/>.</summary>
    public decimal? Position { get; init; }

    /// <summary>The book an after-anchor question is anchored on (a saved Work or a retained source Work/Edition).</summary>
    public CatalogSubjectRef? Anchor { get; init; }

    /// <summary>The ordering to answer in. Unspecified is never treated as publication order; several stated orderings ask the user.</summary>
    public SeriesOrderingKind Order { get; init; } = SeriesOrderingKind.Unspecified;

    public CatalogAnswerFilter Filter { get; init; } = CatalogAnswerFilter.None;

    /// <summary>A retained root from an earlier answer. A follow-up is then answered from its retained entries with no provider call.</summary>
    public Guid? ContextRootId { get; init; }

    /// <summary>A server-issued continuation (from an earlier answer in the same root) to read one more page, spending the same journey.</summary>
    public string? ContinuationToken { get; init; }

    /// <summary>An explicit later choice of one source when offering choices; null lets the service pick one eligible source.</summary>
    public string? ProviderId { get; init; }
}

/// <summary>Deadlines and display bounds. Defaults are the reviewed Phase 4 values, not provider promises.</summary>
public sealed record CatalogAnswerOptions
{
    public static CatalogAnswerOptions Default { get; } = new();

    /// <summary>The overall elapsed bound of one bibliographic answer: resolution, seed reads, waits and parsing. Existing operation/journey limits also apply.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Saved rows read for one saved series or author answer (one look-ahead row is read to know whether more exist).</summary>
    public int SavedPageSize { get; init; } = 100;

    /// <summary>The most choices offered at once (saved and source choices together).</summary>
    public int MaxChoices { get; init; } = 6;

    /// <summary>The most edition titles one expansion returns: the first page only, never a walk through every edition.</summary>
    public int MaxEditionTitles { get; init; } = 20;
}

/// <summary>
/// The edition titles one source reports for a Work the caller already holds. They are edition-title facts and nothing else: no
/// alias, identity proof or translation link is derived from them, and nothing is saved.
/// </summary>
public sealed record CatalogEditionsAnswer(
    CatalogAnswerOutcome Outcome,
    SourceRef? Work,
    string WorkTitle,
    IReadOnlyList<CatalogEditionTitle> Editions,
    IReadOnlyList<string> Qualifiers,
    IReadOnlyList<string> Reasons,
    CatalogAnswerUsage Usage,
    string? ProviderId,
    TimeSpan RetryAfter = default)
{
    public IReadOnlyList<CatalogEditionTitle> Editions { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Editions);

    /// <summary>
    /// Every row in <see cref="Editions"/> is an edition of <see cref="Work"/>, never a Work in its own right: the answer never
    /// mixes the two scopes, and a row's title is never promoted to the Work's title or an alias.
    /// </summary>
    public const string ScopeCode = "edition";

    public IReadOnlyList<string> Qualifiers { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Qualifiers);

    public IReadOnlyList<string> Reasons { get; init => field = DiscoverySnapshots.Of(value); } = DiscoverySnapshots.Of(Reasons);
}

/// <summary>One edition title as an answer carries it: the source's edition id, title, stated language and the field it came from.</summary>
public sealed record CatalogEditionTitle(string? EditionId, string Title, string? Language, string SourceField);

/// <summary>
/// The shared catalog-answer facade used by both web chat and Matrix. It reads only: nothing is written to the catalog, no
/// request, follow, notification, acquisition or delivery state is touched, and no model text is involved. It does not decide who
/// the caller is: the transport authenticates and passes the principal.
/// </summary>
public interface ICatalogAnswerService
{
    Task<CatalogAnswer> AnswerAsync(CatalogAnswerCaller caller, CatalogAnswerRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The edition titles one source reports for a native Work that the caller's own retained answer already holds, read under the
    /// same owner, root and journey allowance as that answer with one request and no retry. <paramref name="entry"/> is the
    /// record the reader selected, taken from that answer's own entries and checked against the retained window here, so a client can
    /// neither name nor reach another reader's record and a stale reference expands nothing. A Work that is not a native source Work,
    /// or a source that has no expansion, answers <see cref="CatalogAnswerOutcome.Unsupported"/> with no provider call; an expired or
    /// foreign root, or a record the retained window does not hold, answers
    /// <see cref="CatalogAnswerOutcome.ContextExpired"/>/<see cref="CatalogAnswerOutcome.InsufficientEvidence"/> and still makes none.
    /// </summary>
    Task<CatalogEditionsAnswer> EditionsAsync(
        CatalogAnswerCaller caller, Guid contextRootId, CatalogSubjectRef entry, CancellationToken cancellationToken);
}
