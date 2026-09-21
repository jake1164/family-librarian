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
    public async Task AQueryIsbnWithoutCandidateIdentifierDoesNotCreateIdentifierConfidence()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync(
            "The Hobbit", "J. R. R. Tolkien", "9780618260300", candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.TitleAuthor, verdicts["ref-1"].Basis);
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
    public async Task NoIsbnFallsBackToTitleAuthorBasis()
    {
        var verifier = NewVerifier();
        IReadOnlyList<ExternalProviderCandidate> candidates =
        [
            ExternalProviderCandidate.FromSimple("ref-1", "The Hobbit", "J. R. R. Tolkien", "epub", 500_000)
        ];

        var verdicts = await verifier.VerifyAsync("The Hobbit", "J. R. R. Tolkien", null, candidates, CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.TitleAuthor, verdicts["ref-1"].Basis);
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

        Assert.IsNull(verdicts["ref-1"].Basis);
        Assert.IsNull(verdicts["ref-2"].Basis);
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
