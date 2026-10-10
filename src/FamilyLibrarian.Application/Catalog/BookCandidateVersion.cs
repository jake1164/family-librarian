using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>Source-supported distinctions for browsing, never an acquisition approval.</summary>
public sealed record BookCandidateVersion(string Kind, string Label, string Explanation)
{
    public static BookCandidateVersion Assess(BookCandidate candidate)
    {
        if (candidate.AssessedVersion is { } assessed) return assessed;
        var titles = candidate.Editions.Count == 1 ? candidate.Editions[0].Title : string.Empty;
        var identity = Text($"{candidate.WorkTitle} {candidate.Title} {titles}");
        var text = Text($"{identity} {candidate.Description} {candidate.Publisher} {string.Join(" ", candidate.Authors)} {string.Join(" ", candidate.Subjects)}");
        if (Has(text, "study guide", "cliffsnotes", "cliffs notes", "cliff notes", "sparknotes", "notes on", "critical essays", "literary criticism", "criticism and interpretation"))
            return new("StudyGuide", "Study guide or criticism", "This record describes material about the book.");
        if (Has(text, "graphic novel", "comic book", "comics", "graphic adaptation"))
            return new("GraphicAdaptation", "Graphic adaptation", "This record describes a comic or graphic version.");
        if (Has(text, "abridged", "abridgement", "abridgment", "simplified", "condensed for young readers"))
            return new("Abridged", "Abridged or simplified", "This record describes a shortened or simplified version.");
        if (Has(text, "retells", "retold", "retelling", "adapted", "adaptation", "dramatization", "dramatisation") || Has(Text(candidate.Publisher), "dramatic pub"))
            return new("Adaptation", "Retelling or adaptation", "This record describes a different telling of the book.");
        if (Has(identity, "omnibus", "trilogy", "collected works") ||
            (candidate.WorkTitle?.Contains(" / ", StringComparison.Ordinal) == true) ||
            (Has(Text(candidate.Description), "contains") && Has(Text(candidate.Description), "short story", "short stories", "the playground", "and the rock cried out")))
            return new("Collection", "Collection or combined volume", "Includes other writings alongside this title.");
        if (Has(Text(candidate.Description), "novel") && candidate.Authors.Count > 0)
            return new("Novel", "Novel", "Described as a novel; edition and completeness may vary.");
        return new("Unspecified", "Version unknown", "The source does not clearly identify the version.");
    }

    public static bool IsRelated(BookCandidate candidate) => Assess(candidate).Kind is not ("Novel" or "Unspecified");

    public static int Rank(BookCandidate candidate, string? query)
    {
        var kind = Assess(candidate).Kind;
        var requested = Assess(candidate with { Title = query ?? string.Empty, WorkTitle = null, Description = null, Publisher = null, Subjects = [], Editions = [], Authors = [], AssessedVersion = null }).Kind;
        return requested != "Unspecified" ? (kind == requested ? 0 : 1) : (IsRelated(candidate) ? 1 : 0);
    }

    private static string Text(string? value) => " " + (string.IsNullOrWhiteSpace(value) ? string.Empty : CatalogText.NormalizeForMatch(value)) + " ";
    private static bool Has(string text, params string[] markers) => markers.Any(marker =>
        text.Contains(" " + marker + " ", StringComparison.Ordinal));
}
