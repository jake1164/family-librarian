using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>Why a provider record is not the same text as a saved Work. A rejection is a typed result, never an exception.</summary>
public enum IdentityRejection
{
    /// <summary>The record's version class differs from the Work's.</summary>
    Class,

    /// <summary>The Work's class is unknown (legacy) and the record is not plain text, or the Work's title alone looks like a different class.</summary>
    ClassUnverifiable,

    /// <summary>The record's known language is not among the Work's known languages.</summary>
    Language,

    /// <summary>The record's known, non-default language cannot be checked against a legacy Work whose languages are all unknown.</summary>
    LanguageUnverifiable,

    /// <summary>Both sides credit authors and none is shared.</summary>
    Author,

    /// <summary>The titles do not reduce to the same work, and no compatible edition title matches an existing one.</summary>
    Title,

    /// <summary>Both sides state an original publication year and they differ by more than one.</summary>
    Year
}

/// <summary>The facts about a saved Work that the identity gates compare a record against. Read inside the catalog lock.</summary>
/// <param name="VersionClass">Null is a legacy Work whose class is unknown.</param>
/// <param name="KnownLanguages">The non-null languages of the Work's editions; derived, there is no Work language column.</param>
/// <param name="OriginalDate">The Work's first-publication date; null when none. A year-only date is its first day.</param>
/// <param name="OriginalBasis">Null is a legacy value, read as a first-publication date.</param>
public sealed record WorkIdentity(
    string Title,
    string? VersionClass,
    IReadOnlySet<string> KnownLanguages,
    IReadOnlyList<string> AuthorNames,
    IReadOnlyList<string> EditionTitles,
    DateOnly? OriginalDate,
    DateBasis? OriginalBasis);

/// <summary>The outcome of the record-level gates: compatible (no contradiction found, not "verified") or the first rejection.</summary>
public readonly record struct IdentityVerdict(IdentityRejection? Rejection)
{
    public bool IsCompatible => Rejection is null;

    public static IdentityVerdict Compatible { get; } = new(null);
}

/// <summary>
/// The pure record-level identity gates shared by every path that could attach a provider record to a saved Work: an ISBN match on
/// an unknown reference, a known-reference refresh, and discovery. A shared identifier never waives them; it only nominates the Work.
/// </summary>
public static class CatalogIdentityPolicy
{
    /// <param name="candidate">The provider record.</param>
    /// <param name="work">The saved Work it would join.</param>
    /// <param name="compatibleEditions">The record's compatible editions (candidate-intrinsic pass); a rejected edition's title proves nothing.</param>
    public static IdentityVerdict EvaluateRecord(
        BookCandidate candidate,
        WorkIdentity work,
        IReadOnlyList<ProjectedEdition> compatibleEditions)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(compatibleEditions);

        return CheckClass(candidate, work)
            ?? CheckLanguage(candidate, work)
            ?? CheckAuthors(candidate, work)
            ?? CheckTitle(candidate, work, compatibleEditions)
            ?? CheckYear(candidate, work)
            ?? IdentityVerdict.Compatible;
    }

    /// <summary>The title reduced to the work it names: packaging dropped, matched case- and punctuation-blind, leading article ignored.</summary>
    public static string TitleKey(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var key = CatalogText.NormalizeForMatch(WorkTitleCore.Reduce(title));
        foreach (var article in LeadingArticles)
        {
            if (key.StartsWith(article, StringComparison.Ordinal))
            {
                return key[article.Length..];
            }
        }

        return key;
    }

    /// <summary>One author name in the form every comparison uses; the empty string for a blank name.</summary>
    public static string AuthorKey(string name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : CatalogText.NormalizeForMatch(AuthorNameForm.Canonical(name));

    private static readonly string[] LeadingArticles = ["the ", "a ", "an "];

    private static IdentityVerdict? CheckClass(BookCandidate candidate, WorkIdentity work)
    {
        var record = CatalogVersionClass.ForRecord(candidate);
        if (work.VersionClass is not null)
        {
            return record == work.VersionClass ? null : new IdentityVerdict(IdentityRejection.Class);
        }

        // A legacy Work's class is unknown: a title alone can only block, never confirm.
        if (record != CatalogVersionClass.Text)
        {
            return new IdentityVerdict(IdentityRejection.ClassUnverifiable);
        }

        return CatalogVersionClass.ForTitleOnly(work.Title) == CatalogVersionClass.Text
            ? null
            : new IdentityVerdict(IdentityRejection.Class);
    }

    private static IdentityVerdict? CheckLanguage(BookCandidate candidate, WorkIdentity work)
    {
        var language = CatalogEditionProjection.EffectiveLanguage(candidate);
        if (language is null)
        {
            return null;
        }

        if (work.KnownLanguages.Count > 0)
        {
            return work.KnownLanguages.Contains(language) ? null : new IdentityVerdict(IdentityRejection.Language);
        }

        return language == CatalogLanguage.Default ? null : new IdentityVerdict(IdentityRejection.LanguageUnverifiable);
    }

    private static IdentityVerdict? CheckAuthors(BookCandidate candidate, WorkIdentity work)
    {
        var theirs = candidate.Authors.Select(AuthorKey).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        var ours = work.AuthorNames.Select(AuthorKey).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (theirs.Count == 0 || ours.Count == 0)
        {
            return null;
        }

        return theirs.Overlaps(ours) ? null : new IdentityVerdict(IdentityRejection.Author);
    }

    private static IdentityVerdict? CheckTitle(BookCandidate candidate, WorkIdentity work, IReadOnlyList<ProjectedEdition> compatibleEditions)
    {
        var workKey = TitleKey(work.Title);
        var titles = new[] { candidate.Title, candidate.WorkTitle }.Where(title => !string.IsNullOrWhiteSpace(title));
        if (workKey.Length > 0 && titles.Any(title => TitleKey(title!) == workKey))
        {
            return null;
        }

        // The alternative route: a compatible edition of the record is titled exactly like an edition the Work already has.
        var existing = work.EditionTitles.Select(NormalizedTitle).Where(title => title.Length > 0).ToHashSet(StringComparer.Ordinal);
        return compatibleEditions.Any(edition => existing.Contains(NormalizedTitle(edition.Edition.Title)))
            ? null
            : new IdentityVerdict(IdentityRejection.Title);
    }

    private static IdentityVerdict? CheckYear(BookCandidate candidate, WorkIdentity work)
    {
        // Only an original/first-publication year can contradict one: an edition's own date legitimately differs by decades.
        if (candidate.PublicationDate is not { } theirs || candidate.PublicationDateBasis == DateBasis.EditionDerived ||
            work.OriginalDate is not { } ours || work.OriginalBasis == DateBasis.EditionDerived)
        {
            return null;
        }

        return Math.Abs(theirs.Year - ours.Year) > 1 ? new IdentityVerdict(IdentityRejection.Year) : null;
    }

    private static string NormalizedTitle(string title) => string.IsNullOrWhiteSpace(title) ? string.Empty : CatalogText.NormalizeForMatch(title);
}
