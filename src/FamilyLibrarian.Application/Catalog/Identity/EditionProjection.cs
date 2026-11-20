namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>Why an edition is kept only as evidence and never written as an Edition of a Work or counted as proof.</summary>
public enum EditionExclusion
{
    /// <summary>The edition has no title, so it cannot be an Edition row.</summary>
    Untitled,

    /// <summary>Its known language differs from the record's, or from every language the Work is known to have.</summary>
    Language,

    /// <summary>Its language is known and not the default, and the legacy Work's languages are all unknown.</summary>
    LanguageUnverifiable,

    /// <summary>Its own version class (abridged, study guide, adaptation, ...) differs from the one it would join.</summary>
    Class,

    /// <summary>Its ISBN already belongs to a different Work. It is never moved and never counts as proof.</summary>
    OwnedByOtherWork,

    /// <summary>Its language is known, but the record's editions state several languages with no majority, so no language view exists.</summary>
    LanguageConflict
}

/// <param name="Edition">The provider's edition, unchanged.</param>
/// <param name="Isbn13">The edition's ISBN-13 when it is a valid 13-digit value; anything else is no ISBN, never a proof.</param>
/// <param name="Exclusion">Null when the edition is compatible: no contradicting evidence was found (not "verified").</param>
public sealed record ProjectedEdition(BookEditionCandidate Edition, string? Isbn13, EditionExclusion? Exclusion)
{
    public bool IsCompatible => Exclusion is null;
}

/// <summary>What a record's editions are judged against: an existing Work, or a Work about to be created.</summary>
/// <param name="ExistingWork">True when attaching to a saved Work; false when creating one, where the Work's own languages are no constraint.</param>
/// <param name="VersionClass">The class the editions must share. A saved Work's class (null is a legacy Work, read as Text); the record's class when creating.</param>
/// <param name="KnownLanguages">The non-null languages already on the Work's editions.</param>
/// <param name="IsbnsOwnedByOtherWorks">ISBN-13s that belong to a different, non-retired Work.</param>
public sealed record ProjectionTarget(
    bool ExistingWork,
    string? VersionClass,
    IReadOnlySet<string> KnownLanguages,
    IReadOnlySet<string> IsbnsOwnedByOtherWorks)
{
    public static ProjectionTarget ForCreation(string recordClass, IReadOnlySet<string> isbnsOwnedByOtherWorks) =>
        new(false, recordClass, new HashSet<string>(StringComparer.Ordinal), isbnsOwnedByOtherWorks);

    public static ProjectionTarget ForWork(
        string? versionClass, IReadOnlySet<string> knownLanguages, IReadOnlySet<string> isbnsOwnedByOtherWorks) =>
        new(true, versionClass, knownLanguages, isbnsOwnedByOtherWorks);
}

/// <summary>The classified editions of one record, and the proof set only compatible editions may supply.</summary>
public sealed record EditionProjection(IReadOnlyList<ProjectedEdition> Editions)
{
    public IReadOnlyList<ProjectedEdition> Compatible { get; } = Editions.Where(edition => edition.IsCompatible).ToArray();

    public IReadOnlyList<ProjectedEdition> Excluded { get; } = Editions.Where(edition => !edition.IsCompatible).ToArray();

    /// <summary>The ISBN-13s of compatible editions, in record order without repeats. Nothing else may establish or contest a match.</summary>
    public IReadOnlyList<string> ProofIsbns { get; } = Editions
        .Where(edition => edition.IsCompatible && edition.Isbn13 is not null)
        .Select(edition => edition.Isbn13!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>The ISBN-13s of excluded editions that no compatible edition also carries.</summary>
    public IReadOnlyList<string> ExcludedOnlyIsbns { get; } = Editions
        .Where(edition => !edition.IsCompatible && edition.Isbn13 is not null)
        .Select(edition => edition.Isbn13!)
        .Except(
            Editions.Where(edition => edition.IsCompatible && edition.Isbn13 is not null).Select(edition => edition.Isbn13!),
            StringComparer.Ordinal)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// The only code that decides which editions of a record may be written as Editions and which ISBNs may match. Each edition is
/// gated on its own: a record-level class and language are not enough, because a record can list a standard edition beside an
/// abridged one, and the edition that supplies proof must itself be one that is allowed to attach.
/// </summary>
public static class CatalogEditionProjection
{
    /// <param name="candidate">The provider record.</param>
    /// <param name="target">Null for the candidate-intrinsic pass that enumerates possible Works; otherwise what it is judged against.</param>
    public static EditionProjection Project(BookCandidate candidate, ProjectionTarget? target)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var (recordLanguage, conflicted) = LanguageViewOf(candidate);
        var recordClass = CatalogVersionClass.ForRecord(candidate);
        var targetClass = target is null ? recordClass : target.VersionClass ?? CatalogVersionClass.Text;

        var projected = new List<ProjectedEdition>(candidate.Editions.Count);
        foreach (var edition in candidate.Editions)
        {
            var isbn = NormalizeIsbn13(edition.Isbn13);
            projected.Add(new ProjectedEdition(edition, isbn, Classify(candidate, edition, isbn, recordLanguage, conflicted, targetClass, target)));
        }

        return new EditionProjection(projected);
    }

    /// <summary>
    /// The language the record is in: its own, else the language most of its editions state (strictly more than half of those that
    /// state one), else unknown. Unknown excludes nothing at the record level; see <see cref="HasConflictingLanguages"/> for the one
    /// unknown that is a known contradiction rather than missing information.
    /// </summary>
    public static string? EffectiveLanguage(BookCandidate candidate) => LanguageViewOf(candidate).Language;

    /// <summary>
    /// True when the record names no language of its own and its editions state two or more different languages with no strict
    /// majority. That is not an unknown language, it is a known disagreement: the record spans translations and cannot say which one
    /// it is. No translation then wins and none is eligible: each edition that states a language is kept as evidence only, so the
    /// record can neither prove a match nor put two translations on one Work. An edition that states no language is the admitted
    /// uncertainty and is judged as before.
    /// </summary>
    public static bool HasConflictingLanguages(BookCandidate candidate) => LanguageViewOf(candidate).Conflicted;

    private static (string? Language, bool Conflicted) LanguageViewOf(BookCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (CatalogLanguage.Normalize(candidate.Language) is { } own)
        {
            return (own, false);
        }

        var stated = candidate.Editions
            .Select(edition => CatalogLanguage.Normalize(edition.Language))
            .Where(language => language is not null)
            .Select(language => language!)
            .ToArray();
        var top = stated.GroupBy(language => language, StringComparer.Ordinal).OrderByDescending(group => group.Count()).FirstOrDefault();
        if (top is not null && top.Count() * 2 > stated.Length)
        {
            return (top.Key, false);
        }

        return (null, stated.Distinct(StringComparer.Ordinal).Count() > 1);
    }

    /// <summary>A valid, normalized ISBN-13, or null. Anything else (blank, wrong length, non-digits) is not an identifier.</summary>
    public static string? NormalizeIsbn13(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: 13 } && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    private static EditionExclusion? Classify(
        BookCandidate candidate,
        BookEditionCandidate edition,
        string? isbn,
        string? recordLanguage,
        bool languagesConflict,
        string targetClass,
        ProjectionTarget? target)
    {
        if (string.IsNullOrWhiteSpace(edition.Title))
        {
            return EditionExclusion.Untitled;
        }

        // 1. Language: against the record's own language, then, for a saved Work, against the languages it is known to have.
        var language = CatalogLanguage.Normalize(edition.Language);
        if (language is not null)
        {
            if (languagesConflict)
            {
                // The record's editions disagree about their language and no majority says which is the book: no translation is
                // chosen, so none of them may be written or counted, whatever the target Work already holds.
                return EditionExclusion.LanguageConflict;
            }

            if (recordLanguage is not null && language != recordLanguage)
            {
                return EditionExclusion.Language;
            }

            if (target is { ExistingWork: true })
            {
                if (target.KnownLanguages.Count > 0 && !target.KnownLanguages.Contains(language))
                {
                    return EditionExclusion.Language;
                }

                if (target.KnownLanguages.Count == 0 && language != CatalogLanguage.Default)
                {
                    return EditionExclusion.LanguageUnverifiable;
                }
            }
        }

        // 2. Class: this edition's own, against the class it would join.
        if (CatalogVersionClass.ForEdition(candidate, edition) != targetClass)
        {
            return EditionExclusion.Class;
        }

        // 3. Ownership: an ISBN held by a different Work stays where it is.
        if (target is not null && isbn is not null && target.IsbnsOwnedByOtherWorks.Contains(isbn))
        {
            return EditionExclusion.OwnedByOtherWork;
        }

        return null;
    }
}
