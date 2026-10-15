using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class CandidateConfidenceTests
{
    [TestMethod]
    [DataRow("Rebeca Yaros", false, "CloseCall")]
    [DataRow(null, false, "LiberalOnly")]
    [DataRow("Yarros", false, "LiberalOnly")]
    [DataRow("Rebecca", false, "LiberalOnly")]
    [DataRow(null, true, "CloseCall")]
    [DataRow("Yarros", true, "CloseCall")]
    [DataRow("Rebecca", true, "CloseCall")]
    [DataRow("Rebeca Yaross", true, "Strong")]
    [DataRow("Rebecca Yaross", false, "NotEligible")]
    [DataRow("David Yarros", true, "NotEligible")]
    public async Task ExactTitlesUseAuthorAndIndependentAgreementTiers(string? author, bool corroborated, string expected)
    {
        var identity = new BookIdentity("Fourth Wing", "Rebecca Yarros", [], Series: [new("The Empyrean", "1")]);
        var candidate = ExternalProviderCandidate.FromSimple("example-record", "Fourth Wing", author, "epub", 1000);
        if (corroborated) candidate = candidate with { Work = candidate.Work with { Series = [new("The Empyrean", "1")] } };
        var verifier = new ExternalProviderMatchVerifier(new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()), new DeterministicBookMatcher());
        var verdict = (await verifier.VerifyAsync(identity, [candidate], CancellationToken.None))[candidate.ProviderReference];
        var option = Option(verdict, candidate);
        Assert.AreEqual(expected, Classify(option));
        Assert.AreEqual(expected == "Strong", Allows("Strict", expected));
        Assert.AreEqual(expected is "Strong" or "CloseCall", Allows("Standard", expected));
        Assert.AreEqual(expected != "NotEligible", Allows("Liberal", expected));
    }

    [TestMethod]
    [DataRow("fuzzy-title")]
    [DataRow("language")]
    [DataRow("condition")]
    [DataRow("blocked")]
    public async Task HardGatesAlwaysOutrankPositiveBasis(string gate)
    {
        var identity = new BookIdentity("Fourth Wing", "Rebecca Yarros", []);
        var candidate = ExternalProviderCandidate.FromSimple("example-record", gate == "fuzzy-title" ? "Fourth Wng" : "Fourth Wing", "Rebecca Yarros", "epub", 1000);
        var verifier = new ExternalProviderMatchVerifier(new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()), new DeterministicBookMatcher());
        var verdict = (await verifier.VerifyAsync(identity, [candidate], CancellationToken.None))[candidate.ProviderReference];
        var option = Option(verdict, candidate);
        if (gate == "language") option = option with { RequiresLanguageConfirmation = true };
        if (gate == "condition") option = option with { RequiresReleaseConfirmation = true, ReleaseConcern = "A sample needs review." };
        if (gate == "blocked") option = option with { AcquisitionAssessment = new(AcquisitionSuitability.Blocked, []) };
        Assert.AreEqual("NotEligible", Classify(option));
    }

    [TestMethod]
    public void APartialAuthorWithoutAnAssessmentHasNoIndependentAgreement()
    {
        var option = Option(new(BookMatchBasis.StrictTitle, false, AuthorAffinity: AuthorAffinity.Evaluate("Rebecca Yarros", "Yarros")),
            ExternalProviderCandidate.FromSimple("example-record", "Fourth Wing", "Yarros", "epub", 1000));
        Assert.AreEqual("LiberalOnly", Classify(option));
    }

    private static FulfillmentOption Option(ExternalProviderMatchVerdict verdict, ExternalProviderCandidate candidate) =>
        new("example-source", candidate.ProviderReference, Guid.Empty, null, RequestMediaType.Ebook, OptionKind.DirectAcquisition,
            AcquisitionMethod.DirectDownload, "epub", "en", null, null, 0m, null, null, "none", null, null,
            MatchBasis: verdict.Basis, AuthorAffinity: verdict.AuthorAffinity, HasPlausibleTitle: verdict.HasPlausibleTitle,
            IdentityAssessment: verdict.IdentityAssessment,
            AcquisitionAssessment: new(verdict.IdentityAssessment?.Decision == WorkIdentityDecision.Mismatch ? AcquisitionSuitability.Blocked : AcquisitionSuitability.EligibleForChecks, []));

    private static string Classify(FulfillmentOption option) => CandidateConfidence.Classify(option).ToString();

    private static bool Allows(string level, string tier) =>
        CandidateConfidence.Allows(Enum.Parse<ProviderConfidenceLevel>(level), Enum.Parse<CandidateConfidenceTier>(tier));
}
