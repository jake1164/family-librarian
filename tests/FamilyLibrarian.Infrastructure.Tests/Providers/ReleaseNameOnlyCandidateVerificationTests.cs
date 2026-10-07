using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// The exact shape a live Prowlarr <c>/search</c> returns (PROVIDER-7): no
/// <c>work</c> object, no legacy flat title/author, nothing but
/// <c>release.name</c> and a format. Before this, every such candidate carried
/// an empty <c>Work.Title</c>, which every matcher rejects outright — so a
/// correct result and a wrong one were indistinguishable and nothing from the
/// provider could ever be fulfilled.
/// </summary>
[TestClass]
public sealed class ReleaseNameOnlyCandidateVerificationTests
{
    private static ExternalProviderMatchVerifier NewVerifier() =>
        new(new BookMatchService(new DeterministicBookMatcher(), new NoOpAmbiguityResolver()), new DeterministicBookMatcher());

    /// <summary>A candidate carrying release evidence only — no work evidence at all.</summary>
    private static ExternalProviderCandidate ReleaseOnly(
        string reference, string releaseName, string? format = "epub", string? language = null) =>
        new(
            reference,
            ExternalProviderWorkEvidence.Empty,
            Edition: language is null ? null : new ExternalProviderEditionEvidence(language, null, null, []),
            Release: new ExternalProviderReleaseEvidence(
                releaseName, format, 9_552_527, IsCollection: null, PartCount: null, IsSample: null,
                IsAbridged: null, IsUnabridged: null, QualityTags: [], AgeDays: 3218));

    [TestMethod]
    public async Task AReleaseNameOnlyCandidateReachesStrictTitleAuthorConfidence()
    {
        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null,
            [ReleaseOnly("c_one", "Fahrenheit 451 by Ray Bradbury EPUB")],
            CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["c_one"].Basis);
    }

    [TestMethod]
    public async Task AStoryCollectionSharingTheTitleAndAuthorIsNotConfirmed()
    {
        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null,
            [ReleaseOnly("c_collection", "Ray Bradbury - A Pleasure to Burn-Fahrenheit 451 Stories (retail) (epub)")],
            CancellationToken.None);

        Assert.AreEqual(WorkIdentityDecision.MatchWithConditions, verdicts["c_collection"].IdentityAssessment!.Decision);
        Assert.AreNotEqual(BookMatchBasis.Identifier, verdicts["c_collection"].Basis);
    }

    [TestMethod]
    public async Task TheRealCapturedEbookSetConfirmsTheSingleTitleAndRefusesTheOthers()
    {
        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null,
            [
                ReleaseOnly("c_epub", "Fahrenheit 451 by Ray Bradbury EPUB"),
                ReleaseOnly("c_multi", "Fahrenheit 451 - Ray Bradbury - eBook [EPUB, MOBI, RTF]"),
                ReleaseOnly("c_tkrg", "Ray Bradbury - Fahrenheit 451 - epub [TKRG]"),
                ReleaseOnly("c_anniv", "Ray Bradbury - Fahrenheit 451 (60th Anniversary) (retail)(epub)"),
                ReleaseOnly("c_stories", "Ray Bradbury - A Pleasure to Burn-Fahrenheit 451 Stories (retail) (epub)"),
                ReleaseOnly("c_graphic", "Ray Bradbury's Fahrenheit 451 Graphic Novel", format: null)
            ],
            CancellationToken.None);

        // Each genuine copy is confirmed...
        foreach (var reference in new[] { "c_epub", "c_multi", "c_tkrg", "c_anniv" })
        {
            Assert.AreEqual(
                BookMatchBasis.StrictTitleAuthor, verdicts[reference].Basis,
                $"'{reference}' is a genuine copy of the requested work.");
        }

        // ...and the two different products are not, even though both contain
        // the requested title and the requested author.
        Assert.AreEqual(WorkIdentityDecision.MatchWithConditions, verdicts["c_stories"].IdentityAssessment!.Decision);
        Assert.IsTrue(verdicts["c_stories"].IdentityAssessment!.Conditions.Count > 0);
        Assert.AreEqual(WorkIdentityDecision.MatchWithConditions, verdicts["c_graphic"].IdentityAssessment!.Decision);
        Assert.IsTrue(verdicts["c_graphic"].IdentityAssessment!.Conditions.Count > 0);
    }

    [TestMethod]
    public async Task AForeignLanguageReleaseIsNotConfirmedForAnEnglishRequest()
    {
        // The release name is the only place this candidate's language appears;
        // treating "Spanish" as a release tag would auto-acquire it.
        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null,
            [ReleaseOnly("c_spanish", "Ray.Bradbury-Fahrenheit.451.2012.Spanish.Retail.EPUB.eBook-BitBook")],
            CancellationToken.None);

        Assert.AreNotEqual(BookMatchBasis.StrictTitleAuthor, verdicts["c_spanish"].Basis);
        Assert.AreEqual("es", verdicts["c_spanish"].ReleaseNameLanguage);
    }

    [TestMethod]
    public async Task AnAudiobookNarrationCreditSurvivesVerification()
    {
        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null,
            [ReleaseOnly("c_audio", "Fahrenheit 451 by Ray Bradbury (read by Christopher Hurt)", format: null)],
            CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["c_audio"].Basis);
        Assert.AreEqual("Christopher Hurt", verdicts["c_audio"].ReleaseNameNarrator);
    }

    [TestMethod]
    public async Task StructuredWorkEvidenceStillWinsOverTheReleaseName()
    {
        // The release name is a fallback, not a replacement: a provider that
        // does report work evidence must keep being judged on it.
        var candidate = new ExternalProviderCandidate(
            "c_structured",
            new ExternalProviderWorkEvidence("Fahrenheit 451", null, [new BookAuthor("Ray Bradbury", "author")], [], []),
            Release: new ExternalProviderReleaseEvidence(
                "some.unhelpful.release.string", "epub", 1_000, null, null, null, null, null, [], null));

        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null, [candidate], CancellationToken.None);

        Assert.AreEqual(BookMatchBasis.StrictTitleAuthor, verdicts["c_structured"].Basis);
    }

    [TestMethod]
    public async Task ACandidateWithNoWorkEvidenceAndNoReleaseNameIsStillUnconfirmed()
    {
        var candidate = new ExternalProviderCandidate("c_empty", ExternalProviderWorkEvidence.Empty);

        var verdicts = await NewVerifier().VerifyAsync(
            "Fahrenheit 451", "Ray Bradbury", isbn13: null, [candidate], CancellationToken.None);

        Assert.IsNull(verdicts["c_empty"].Basis);
    }
}
