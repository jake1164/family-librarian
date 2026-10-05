using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ExternalProviderMatchVerifierTests
{
    private static ExternalProviderMatchVerifier NewVerifier() =>
        new(new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()), new DeterministicBookMatcher());

    [TestMethod]
    public async Task ACandidateWithTheRequestedIsbnAndCorroboratingTitleAuthorIsIdentifierBasis()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            CandidateWithIsbn("ref-1", "The Hobbit", "J. R. R. Tolkien", "9780618260300")
        ];

        var verdicts = await verifier.VerifyAsync(
            "The Hobbit", "J. R. R. Tolkien", "9780618260300", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.Identifier, verdicts["ref-1"].Basis);
        Assert.IsFalse(verdicts["ref-1"].RequiresLanguageConfirmation);
    }

    [TestMethod]
    public async Task AQueryIsbnWithoutCandidateIdentifierCanStillUseStrictTitleAuthorConfidence()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync(
            "The Hobbit", "J. R. R. Tolkien", "9780618260300", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["ref-1"].Basis);
    }

    [TestMethod]
    public async Task ACandidateWithMatchingIdentifierButWrongTitleIsNotTrustedAsIdentifierBasis()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            CandidateWithIsbn("ref-1", "Dim Sum of Fears", "Some Other Author", "9780002224988")
        ];

        var verdicts = await verifier.VerifyAsync(
            "The Sum of All Fears", "Tom Clancy", "9780002224988", candidates, CancellationToken.None);

        Assert.IsNull(verdicts["ref-1"].Basis);
    }

    [TestMethod]
    public async Task NoIsbnWithExactObservedTitleAndAuthorUsesStrictTitleAuthorBasis()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync("The Hobbit", "J. R. R. Tolkien", null, candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["ref-1"].Basis);
    }

    [TestMethod]
    public async Task AmbiguousCandidatesAreAllUnconfirmed()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000),
            ExternalProviderCandidate.FromSimple("ref-2", "The Hobbit", "J. R. R. Tolkien", "pdf", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync("The Hobbit", "J. R. R. Tolkien", null, candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["ref-1"].Basis);
        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["ref-2"].Basis);
    }

    [TestMethod]
    public async Task NoMatchingCandidateIsUnconfirmed()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "An Unrelated Title", "Someone Else", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync("The Hobbit", "J. R. R. Tolkien", null, candidates, CancellationToken.None);

        Assert.IsNull(verdicts["ref-1"].Basis);
        Assert.IsFalse(verdicts["ref-1"].RequiresLanguageConfirmation);
    }

    [TestMethod]
    public async Task NoCandidatesReturnsAnEmptyVerdictSet()
    {
        var verifier = NewVerifier();

        var verdicts = await verifier.VerifyAsync("The Hobbit", "J. R. R. Tolkien", null, [], CancellationToken.None);

        Assert.AreEqual(0, verdicts.Count);
    }

    [TestMethod]
    public async Task ASourceTitleWithVerifiedTrailingByAuthorUsesStrictTitleAuthorBasis()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "Net force by Tom Clancy", "Clancy, Tom", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync("Net Force", "Tom Clancy", null, candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["ref-1"].Basis);
    }

    [TestMethod]
    public async Task ARecordedEditionTitleCanCorroborateOneCandidateWithoutTrustingOtherBooksByTheAuthor()
    {
        var verifier = NewVerifier();
        var identity = new BookIdentity(
            "Mekhanicheskiĭ apelʹsin", "Anthony Burgess", [],
            AlternateTitles: ["A Clockwork Orange"]);
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple(
                "clockwork-orange", "A Clockwork Orange", "Anthony Burgess", "epub", 500_000),
            ExternalProviderCandidate.FromSimple(
                "unrelated-burgess", "ABBA ABBA", "Anthony Burgess", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync(identity, candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["clockwork-orange"].Basis);
        Assert.IsNull(verdicts["unrelated-burgess"].Basis);
    }

    [TestMethod]
    public async Task ASubtitleOrDerivativeTitleRemainsTheReviewableTitleAuthorTier()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "The Hobbit: A Novel", "J. R. R. Tolkien", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync("The Hobbit", "J. R. R. Tolkien", null, candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.TitleAuthor, verdicts["ref-1"].Basis);
    }

    [TestMethod]
    public async Task BroadRetrievalKeepsEveryAuthorRepresentationForLocalEvaluation()
    {
        var names = new[]
        {
            "Rebecca Yarros-The Empyrean-Fourth Wing Part 2-Extended",
            "Fourth.Wing.by.Rececca.Yarros",
            "req.Fourth.Wing.Fourth.Wing.Book.1.m4b",
            "Fourth Wing - Stephen King"
        };
        var candidates = names.Select((name, index) => new ExternalProviderCandidate(
            $"ref-{index}", new ExternalProviderWorkEvidence(string.Empty, null, [], [], []),
            Release: new ExternalProviderReleaseEvidence(name, "m4b", null, false, 1, false, null, null, [], null))).ToArray();
        var verdicts = await NewVerifier().VerifyAsync("Fourth Wing", "Rebecca Yarros", null, candidates, CancellationToken.None);
        Assert.HasCount(4, verdicts);
        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["ref-1"].Basis);
        Assert.IsNull(verdicts["ref-0"].Basis, "Multipart/extended evidence must still require review.");
        Assert.IsNull(verdicts["ref-2"].Basis);
        Assert.IsNull(verdicts["ref-3"].Basis);
        Assert.IsTrue(verdicts["ref-1"].AuthorAffinity!.Score > verdicts["ref-2"].AuthorAffinity!.Score);
        Assert.AreEqual(AuthorAffinityKind.Unknown, verdicts["ref-2"].AuthorAffinity!.Kind);
        Assert.AreEqual(AuthorAffinityKind.Conflict, verdicts["ref-3"].AuthorAffinity!.Kind);
    }

    [TestMethod]
    public async Task StructuredConflictCannotBeOverriddenByAnExactReleaseName()
    {
        var candidate = ExternalProviderCandidate.FromSimple("conflict", "Fourth Wing", "Stephen King", "m4b", 1000)
            with { Release = new ExternalProviderReleaseEvidence("Fourth Wing - Rebecca Yarros", "m4b", null, false, 1, false, null, null, [], null) };
        var verdicts = await NewVerifier().VerifyAsync("Fourth Wing", "Rebecca Yarros", null, [candidate], CancellationToken.None);
        Assert.IsNull(verdicts["conflict"].Basis);
        Assert.AreEqual(AuthorAffinityKind.Conflict, verdicts["conflict"].AuthorAffinity!.Kind);
    }

    [TestMethod]
    public async Task IncompleteStructuredAuthorCanUseReleaseEvidenceButConflictsStillRequireReview()
    {
        var partial = ExternalProviderCandidate.FromSimple("partial", "Fourth Wing", "Rebecca", "m4b", 1000)
            with { Release = new ExternalProviderReleaseEvidence("Fourth Wing - R. Yarros", "m4b", null, false, 1, false, null, null, [], null) };
        var conflict = partial with { ProviderReference = "conflict", Release = partial.Release! with { Name = "Fourth Wing - Stephen King" } };
        var verdicts = await NewVerifier().VerifyAsync("Fourth Wing", "Rebecca Yarros", null, [partial, conflict], CancellationToken.None);
        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["partial"].Basis);
        Assert.IsTrue(verdicts["partial"].HasPlausibleTitle);
        Assert.IsNull(verdicts["conflict"].Basis);
        Assert.AreEqual(AuthorAffinityKind.Conflict, verdicts["conflict"].AuthorAffinity!.Kind);
    }

    [TestMethod]
    public async Task IdentifierEvidenceCannotOverrideConflictingAuthorEvidence()
    {
        var candidate = CandidateWithIsbn("conflict", "Fourth Wing", "Rebecca Yarros Stephen King", "9780618260300");
        var verdicts = await NewVerifier().VerifyAsync("Fourth Wing", "Rebecca Yarros", "9780618260300", [candidate], CancellationToken.None);
        Assert.AreNotEqual(BookMatchBasis.Identifier, verdicts["conflict"].Basis);
        Assert.AreNotEqual(BookMatchBasis.StrictTitleAuthor, verdicts["conflict"].Basis);
        Assert.AreEqual(AuthorAffinityKind.Conflict, verdicts["conflict"].AuthorAffinity!.Kind);
    }

    private static ExternalProviderCandidate CandidateWithIsbn(
        string providerReference, string title, string author, string isbn13) =>
        new(
            providerReference,
            new ExternalProviderWorkEvidence(
                title, null, [new BookAuthor(author, "author")], [], []),
            new ExternalProviderEditionEvidence(
                "en", null, null, [new BookIdentifier("isbn13", isbn13)]),
            new ExternalProviderReleaseEvidence(
                null, "epub", 500_000, false, 1, false, null, null, [], null,
                ExternalProviderDrmStatus.None));
}
