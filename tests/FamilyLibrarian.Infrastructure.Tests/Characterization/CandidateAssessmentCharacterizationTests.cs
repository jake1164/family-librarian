using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Characterization;

/// <summary>
/// M1-0: pins how <see cref="DeterministicCandidateIdentityResolver"/> classifies
/// release candidates today. Fictional work: "The Lantern Road" by Mara Quill,
/// book 3 of "The Ember Cycle". Nothing here changes matching behavior.
/// </summary>
[TestClass]
public sealed class CandidateAssessmentCharacterizationTests
{
    internal static readonly BookIdentity Requested = new(
        CharacterizationFixtures.Title, CharacterizationFixtures.Author, [],
        Series: [new BookSeries("The Ember Cycle", "3")], Language: "en");

    private static ExternalProviderCandidate Candidate(
        string releaseName, string? structuredTitle = null, string? structuredAuthor = null,
        string? seriesPosition = null, string? language = null, bool? collection = null,
        bool? sample = null, bool? abridged = null, bool? unabridged = null) =>
        new("fixture",
            new ExternalProviderWorkEvidence(
                structuredTitle ?? string.Empty, null,
                structuredAuthor is null ? [] : [new BookAuthor(structuredAuthor, "author")],
                seriesPosition is null ? [] : [new BookSeries("The Ember Cycle", seriesPosition)], []),
            language is null ? null : new ExternalProviderEditionEvidence(language, null, null, []),
            new ExternalProviderReleaseEvidence(releaseName, "m4b", null, collection, 1, sample, abridged, unabridged, [], null));

    public static string Describe(CandidateIdentityAssessment a) =>
        $"{a.Decision}; title={a.TitleEvidence.State}; author={a.AuthorEvidence.Kind}; lang={a.LanguageEvidence}; " +
        $"contradictions={a.Contradictions.Count}; conditions=[{string.Join(",", a.Conditions.Select(c => c.Kind).OrderBy(k => k))}]";

    internal static IReadOnlyList<(CharacterizationCase Case, ExternalProviderCandidate Input)> Cases { get; } =
    [
        (new("C01", "Exact title and author in the release name", "Match; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[]"),
            Candidate("Mara Quill - The Lantern Road (Unabridged)")),
        (new("C02", "Series neighbor by the same author (book 2 title)", "Mismatch; title=Conflicting; author=Exact; lang=Unknown; contradictions=2; conditions=[]"),
            Candidate("Mara Quill - The Ash Gate", "The Ash Gate", "Mara Quill", "2")),
        (new("C03", "Requested title but structured series position says book 2", "Mismatch; title=Exact; author=Exact; lang=Unknown; contradictions=1; conditions=[]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill", "2")),
        (new("C04", "Author collision: same title, different author", "Mismatch; title=Exact; author=Conflict; lang=Unknown; contradictions=1; conditions=[]"),
            Candidate("The Lantern Road by Mara Quinn")),
        (new("C05", "Omnibus named in the release, requested work listed inside", "MatchWithConditions; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[EditionReview,EditionReview]"),
            Candidate("Mara Quill - The Ember Cycle Omnibus Books 1-3 (The Lantern Road)")),
        (new("C06", "Structured collection flag with an exact title", "MatchWithConditions; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[EditionReview]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill", collection: true)),
        (new("C07", "Structured sample flag with an exact title", "MatchWithConditions; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[EditionReview]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill", sample: true)),
        (new("C08", "Release name carries a sample cue only", "MatchWithConditions; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[EditionReview]"),
            Candidate("Mara Quill - The Lantern Road (Sample)")),
        (new("C09", "Structured abridged flag", "MatchWithConditions; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[EditionReview]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill", abridged: true)),
        (new("C10", "Edition declares a non-requested language", "Mismatch; title=Exact; author=Exact; lang=Conflicting; contradictions=1; conditions=[]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill", language: "de")),
        (new("C11", "Language unknown: unknown is not a contradiction", "Match; title=Exact; author=Exact; lang=Unknown; contradictions=0; conditions=[]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill")),
        (new("C12", "Obscure work: strong identity, no size/age/popularity data", "Match; title=Exact; author=Exact; lang=Compatible; contradictions=0; conditions=[]"),
            Candidate("Mara Quill - The Lantern Road", CharacterizationFixtures.Title, "Mara Quill", language: "en")),
        (new("C13", "Title only, no author evidence anywhere", "Match; title=Exact; author=Unknown; lang=Unknown; contradictions=0; conditions=[]"),
            Candidate("The Lantern Road")),
        (new("C14", "Release name unrelated to the request", "Ambiguous; title=Unknown; author=Unknown; lang=Unknown; contradictions=0; conditions=[MalformedStructure]", "Ambiguous; title=Unknown; author=Unknown; lang=Unknown; contradictions=0; conditions=[] (insufficient evidence, not a malformed-structure reason)"),
            Candidate("Completely Different Book - Someone Else")),
    ];

    [TestMethod]
    public void CurrentCandidateClassificationMatchesTheBaseline()
    {
        var failures = new List<string>();
        foreach (var (fixture, input) in Cases)
        {
            var actual = Describe(DeterministicCandidateIdentityResolver.Assess(Requested, input));
            if (actual != fixture.Observed) failures.Add($"{fixture.Id} [{fixture.Description}]: observed \"{actual}\"");
        }

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
