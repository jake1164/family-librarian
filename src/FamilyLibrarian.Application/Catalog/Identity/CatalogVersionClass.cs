namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>
/// The identity class of a text, built from the vocabulary <see cref="BookCandidateVersion"/> really returns. A novel and a record
/// whose version is unspecified are both ordinary text; every other kind keeps its name, so two records attach only when equal.
/// </summary>
public static class CatalogVersionClass
{
    public const string Text = "Text";

    /// <summary>The class of the whole record, exactly as search classifies it.</summary>
    public static string ForRecord(BookCandidate candidate) =>
        FromKind(BookCandidateVersion.Assess(candidate with { AssessedVersion = null }).Kind);

    /// <summary>
    /// The class of one edition: the classifier's own single-edition branch, so a marker in one edition's title or publisher is
    /// not hidden by a record that lists several. Record-level description and subjects still apply to every edition.
    /// </summary>
    public static string ForEdition(BookCandidate candidate, BookEditionCandidate edition) =>
        FromKind(BookCandidateVersion.Assess(candidate with
        {
            Editions = [edition],
            Publisher = edition.Publisher ?? candidate.Publisher,
            AssessedVersion = null
        }).Kind);

    /// <summary>
    /// What a title alone can show. The classifier needs a description, publisher and subjects that a saved Work does not keep, so
    /// this can only block a legacy Work, never confirm one.
    /// </summary>
    public static string ForTitleOnly(string title) =>
        FromKind(BookCandidateVersion.Assess(new BookCandidate(
            string.Empty, string.Empty, string.Empty, title, [], null, null, null, [], [])).Kind);

    public static string FromKind(string kind) => kind is "Novel" or "Unspecified" ? Text : kind;
}
