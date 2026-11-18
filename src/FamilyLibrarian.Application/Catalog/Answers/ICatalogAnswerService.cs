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
}

/// <summary>
/// The shared catalog-answer facade used by both web chat and Matrix. It reads only: nothing is written to the catalog, no
/// request, follow, notification, acquisition or delivery state is touched, and no model text is involved. It does not decide who
/// the caller is: the transport authenticates and passes the principal.
/// </summary>
public interface ICatalogAnswerService
{
    Task<CatalogAnswer> AnswerAsync(CatalogAnswerCaller caller, CatalogAnswerRequest request, CancellationToken cancellationToken);
}
