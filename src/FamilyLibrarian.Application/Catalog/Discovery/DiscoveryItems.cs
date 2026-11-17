using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Application.Catalog.Identity;

namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>Whether a source stated a date at all. Unknown, known-but-undated and dated are three different facts.</summary>
public enum PublicationDateState
{
    /// <summary>The source said nothing about a date. This is not evidence there is none.</summary>
    NotStated = 0,

    /// <summary>The source lists the book as known or announced but gives no date.</summary>
    KnownUndated = 1,

    Dated = 2
}

/// <summary>
/// Everything a source said about when a book was published, with the meaning of the date kept beside it. Reuses the
/// Phase 2 precision and basis types; a year-only value is carried as the first day of that year, as everywhere else.
/// </summary>
public sealed record PublicationEvidence
{
    private PublicationEvidence(
        PublicationDateState state, DateOnly? date, DatePrecision? precision, DateBasis basis, PublicationStatus sourceStatus)
    {
        State = state;
        Date = date;
        Precision = precision;
        Basis = basis;
        SourceStatus = sourceStatus;
    }

    public static PublicationEvidence NotStated { get; } = new(PublicationDateState.NotStated, null, null, DateBasis.Unknown, PublicationStatus.Unknown);

    public PublicationDateState State { get; }

    public DateOnly? Date { get; }

    public DatePrecision? Precision { get; }

    /// <summary>Unknown unless the source's statement is verified as the original or an edition date. Never assumed original.</summary>
    public DateBasis Basis { get; }

    /// <summary>The source's own published/forthcoming statement. Never derived here from the current clock.</summary>
    public PublicationStatus SourceStatus { get; }

    public static PublicationEvidence KnownUndated(PublicationStatus sourceStatus = PublicationStatus.Forthcoming) =>
        new(PublicationDateState.KnownUndated, null, null, DateBasis.Unknown, sourceStatus);

    public static PublicationEvidence Dated(
        DateOnly date, DatePrecision precision, DateBasis basis, PublicationStatus sourceStatus = PublicationStatus.Unknown)
    {
        if (!Enum.IsDefined(precision) || !Enum.IsDefined(basis) || !Enum.IsDefined(sourceStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "Precision, basis and status must be defined values.");
        }

        return new PublicationEvidence(PublicationDateState.Dated, date, precision, basis, sourceStatus);
    }
}

/// <summary>Whether a source-stated alternate title belongs to the Work or to one Edition (for example a translation).</summary>
public enum TitleScope
{
    Work = 1,
    Edition = 2
}

/// <summary>
/// A title a source states in addition to the one it reports, kept with where it came from. It is evidence, never an alias: it is not
/// merged into the reported title, not used to match records, and a translation's title is never folded into the original's.
/// </summary>
/// <param name="Text">The alternate title exactly as the source gave it.</param>
/// <param name="SourceField">The source's own field name (for example <c>alternative_title</c>).</param>
/// <param name="Language">The language the source states for this title; null when it states none (never guessed).</param>
/// <param name="Scope">Whether the source attaches the title to the Work or to one Edition.</param>
public sealed record AlternateTitle(string Text, string SourceField, string? Language, TitleScope Scope);

/// <summary>A title exactly as reported, plus any normalization applied and why, so the original is never lost.</summary>
/// <param name="Reported">The source's title text, unchanged.</param>
/// <param name="Normalized">The form used for matching or display when a rule changed it; null when nothing was changed.</param>
/// <param name="RuleCode">Stable code of the explicit rule applied (for example <c>series-suffix</c>); null with no change.</param>
public sealed record TitleForm(string Reported, string? Normalized = null, string? RuleCode = null)
{
    private static readonly IReadOnlyList<AlternateTitle> NoAlternates = DiscoverySnapshots.Of<AlternateTitle>([]);

    private IReadOnlyList<AlternateTitle> alternates = NoAlternates;

    public bool WasNormalized => Normalized is not null;

    /// <summary>
    /// Alternate titles the source stated, in source order; empty when it stated none (which says nothing about whether others exist).
    /// Setting it stores a private read-only copy, whether through an initializer or <c>with</c>: the caller who built the list or
    /// array keeps no handle that can change this form afterwards. Null is rejected rather than read as "none".
    /// </summary>
    public IReadOnlyList<AlternateTitle> Alternates
    {
        get => alternates;
        init => alternates = DiscoverySnapshots.Of(value);
    }

    // Value equality over the list's contents, so two forms with the same alternates are equal whatever list instance holds them.
    public bool Equals(TitleForm? other) =>
        other is not null && Reported == other.Reported && Normalized == other.Normalized && RuleCode == other.RuleCode &&
        Alternates.SequenceEqual(other.Alternates);

    public override int GetHashCode() => HashCode.Combine(Reported, Normalized, RuleCode, Alternates.Count);
}

/// <summary>How a book belongs to a series. A role comes only from an explicit statement; featured/primary never implies Main.</summary>
public enum MembershipRole
{
    Unknown = 0,
    Main = 1,
    Companion = 2
}

/// <summary>One claimed place in a series. A member may carry several claims; ties and conflicts are kept, not resolved.</summary>
public sealed record PositionClaim(string? Label, decimal? Sort, SeriesOrderingKind? Ordering = null, SourceRef? Series = null)
{
    public SourceRef? Series { get; } = Series is null || Series.Kind == ExternalReferenceEntityType.Series
        ? Series
        : throw new ArgumentException("A position claim's series must have the Series kind.", nameof(Series));
}

/// <summary>
/// The compatible candidate/evidence fields every discovered book carries: a Work-or-Edition source reference, the title as
/// reported, ordinal-safe credits, language, format, publication evidence and when it was observed. Membership role,
/// Work version and media format are independent facts and are not folded into one enum.
/// </summary>
public sealed record DiscoveredBook
{
    public DiscoveredBook(
        SourceRef source,
        TitleForm title,
        IReadOnlyList<CreditRef> credits,
        PublicationEvidence publication,
        DateTimeOffset observedAtUtc,
        string? language = null,
        string? format = null,
        BookCandidateVersion? version = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(publication);
        if (source.Kind is not (ExternalReferenceEntityType.Work or ExternalReferenceEntityType.Edition))
        {
            throw new ArgumentException("A discovered book must be a Work or an Edition.", nameof(source));
        }

        if (string.IsNullOrWhiteSpace(title.Reported))
        {
            throw new ArgumentException("A discovered book needs its reported title.", nameof(title));
        }

        Source = source;
        Title = title;
        Credits = DiscoverySnapshots.Of(credits);
        Publication = publication;
        ObservedAtUtc = observedAtUtc;
        Language = language;
        Format = format;
        Version = version;
    }

    /// <summary>Distinguishes a Work from an Edition by <see cref="SourceRef.Kind"/>; a series member may be either.</summary>
    public SourceRef Source { get; }

    public TitleForm Title { get; }

    public IReadOnlyList<CreditRef> Credits { get; }

    public PublicationEvidence Publication { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string? Language { get; }

    /// <summary>Media/edition format as reported; independent of version and of membership role.</summary>
    public string? Format { get; }

    /// <summary>Work version (collection, adaptation, abridgment, ...) when the source supports a distinction; null is not assessed.</summary>
    public BookCandidateVersion? Version { get; }
}

public sealed record SeriesMember(
    DiscoveredBook Book,
    SourceRef? Series,
    IReadOnlyList<PositionClaim> Positions,
    MembershipRole Role = MembershipRole.Unknown)
{
    public IReadOnlyList<PositionClaim> Positions { get; } = DiscoverySnapshots.Of(Positions);
}

/// <summary>A series a book is claimed to belong to, with that book's position claim there.</summary>
public sealed record BookSeriesClaim(SeriesRef Series, PositionClaim? Position, MembershipRole Role = MembershipRole.Unknown);

public sealed record AuthorWork(DiscoveredBook Book, IReadOnlyList<BookSeriesClaim> Series)
{
    public IReadOnlyList<BookSeriesClaim> Series { get; } = DiscoverySnapshots.Of(Series);
}

/// <summary>A series reached from an author. The series ref may be native while the author-to-series edge is derived.</summary>
public sealed record AuthorSeriesEntry(SeriesRef Series, RelationshipEvidence Relationship);

/// <summary>
/// How a discovery item's credits may feed the unchanged Phase 2 writer, which matches <see cref="BookCandidate.AuthorCredits"/>
/// to <see cref="BookCandidate.Authors"/> by normalised name. Anything that name match could cross-assign is unprojectable
/// and stays read-only preview data.
/// </summary>
public sealed record LegacyCreditProjection(
    IReadOnlyList<string> Authors,
    IReadOnlyList<BookAuthorCredit> Credits,
    bool Projectable,
    string? ReasonCode)
{
    public const string AmbiguousSameName = "credit-same-name-ambiguous";

    public IReadOnlyList<string> Authors { get; } = DiscoverySnapshots.Of(Authors);

    public IReadOnlyList<BookAuthorCredit> Credits { get; } = DiscoverySnapshots.Of(Credits);

    /// <summary>
    /// Only author-role credits (no role, or "author") become legacy authors. Other roles keep their own ids in the discovery
    /// item and are never attached to a namesake author. Two author credits whose names match but whose ids differ (or where
    /// only one has an id) make the whole projection unprojectable rather than letting the first match win.
    /// </summary>
    public static LegacyCreditProjection From(IReadOnlyList<CreditRef> credits)
    {
        ArgumentNullException.ThrowIfNull(credits);
        var authorCredits = credits
            .Where(credit => !string.IsNullOrWhiteSpace(credit.Label) && IsAuthorRole(credit.Role))
            .OrderBy(credit => credit.Ordinal)
            .ToArray();

        foreach (var group in authorCredits.GroupBy(credit => CatalogIdentityPolicy.AuthorKey(credit.Label), StringComparer.Ordinal))
        {
            if (group.Select(credit => credit.AuthorId).Distinct().Count() > 1)
            {
                return new LegacyCreditProjection([], [], false, AmbiguousSameName);
            }
        }

        var names = new List<string>();
        var details = new List<BookAuthorCredit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credit in authorCredits)
        {
            if (!seen.Add(CatalogIdentityPolicy.AuthorKey(credit.Label)))
            {
                continue;
            }

            names.Add(credit.Label.Trim());
            details.Add(new BookAuthorCredit(credit.Label.Trim(), credit.Role, credit.AuthorId?.ExternalId));
        }

        return new LegacyCreditProjection(names, details, true, null);
    }

    private static bool IsAuthorRole(string? role) =>
        string.IsNullOrWhiteSpace(role) || string.Equals(role.Trim(), "author", StringComparison.OrdinalIgnoreCase);
}
