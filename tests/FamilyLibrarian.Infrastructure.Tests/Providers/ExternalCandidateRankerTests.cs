using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// PROVIDER-7: more than one acceptable copy existing is not a decision Family
/// Librarian hands back to a person. The chain must always produce one winner
/// and a reproducible fallback order.
/// </summary>
[TestClass]
public sealed class ExternalCandidateRankerTests
{
    private static FulfillmentOption Candidate(
        string resultId,
        BookMatchBasis? basis = BookMatchBasis.StrictTitleAuthor,
        string? format = "epub",
        string? quality = null,
        bool requiresReleaseConfirmation = false,
        string? releaseConcern = null,
        RequestMediaType mediaType = RequestMediaType.Ebook,
        NarrationKind? narrationKind = null) =>
        new(
            ProviderId: "prowlarr",
            ProviderResultId: resultId,
            WorkId: Guid.Empty,
            EditionId: null,
            MediaType: mediaType,
            OptionKind: OptionKind.DirectAcquisition,
            AcquisitionMethod: AcquisitionMethod.DirectDownload,
            Format: format,
            Language: null,
            Quality: quality,
            Availability: null,
            Cost: 0m,
            Currency: null,
            LicenseOrUsageStatus: null,
            DrmStatus: "unknown",
            ExternalActionUri: null,
            ProviderData: resultId,
            MatchBasis: basis,
            RequiresReleaseConfirmation: requiresReleaseConfirmation,
            ReleaseConcern: releaseConcern,
            NarrationKind: narrationKind);

    [TestMethod]
    public void IdentifierEvidenceOutranksStrictTitleAndAuthor()
    {
        var ranked = ExternalCandidateRanker.Rank(
            [Candidate("b"), Candidate("a", BookMatchBasis.Identifier)], RequestMediaType.Ebook);

        Assert.AreEqual("a", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void StrictTitleAndAuthorOutranksTheBroadFallback()
    {
        var ranked = ExternalCandidateRanker.Rank(
            [Candidate("a", BookMatchBasis.TitleAuthor), Candidate("b", BookMatchBasis.StrictTitleAuthor)],
            RequestMediaType.Ebook);

        Assert.AreEqual("b", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void IdentityOutweighsEveryLaterDimension()
    {
        // The reason this is a comparator chain and not an additive score: a
        // weaker identity must not win on format and provenance combined.
        var ranked = ExternalCandidateRanker.Rank(
            [
                Candidate("weak-but-pretty", BookMatchBasis.TitleAuthor, format: "epub", quality: "retail"),
                Candidate("strong-but-plain", BookMatchBasis.StrictTitleAuthor, format: "azw")
            ],
            RequestMediaType.Ebook);

        Assert.AreEqual("strong-but-plain", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void ACleanReleaseOutranksOneNeedingConfirmation()
    {
        var ranked = ExternalCandidateRanker.Rank(
            [
                Candidate("concerning", requiresReleaseConfirmation: true, releaseConcern: "This release is reported as a sample/preview, not the complete work."),
                Candidate("clean")
            ],
            RequestMediaType.Ebook);

        Assert.AreEqual("clean", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void AnUnconfirmedDrmConcernOutranksAnyOtherReleaseConcern()
    {
        // The acquisition path already tolerates unknown DRM under
        // download-time validation, so it is the lesser of the two concerns.
        var ranked = ExternalCandidateRanker.Rank(
            [
                Candidate("sample", requiresReleaseConfirmation: true, releaseConcern: "This release is reported as a sample/preview, not the complete work."),
                Candidate("unknown-drm", requiresReleaseConfirmation: true, releaseConcern: ExternalReleasePolicy.UnknownDrmConfirmationReason)
            ],
            RequestMediaType.Ebook);

        Assert.AreEqual("unknown-drm", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void EpubIsPreferredOverOtherAcceptedEbookFormats()
    {
        var ranked = ExternalCandidateRanker.Rank(
            [Candidate("mobi-one", format: "mobi"), Candidate("epub-one", format: "epub")],
            RequestMediaType.Ebook);

        Assert.AreEqual("epub-one", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void ARetailTaggedReleaseWinsAnOtherwiseExactTie()
    {
        var ranked = ExternalCandidateRanker.Rank(
            [Candidate("aaa-untagged"), Candidate("zzz-retail", quality: "retail")],
            RequestMediaType.Ebook);

        // "aaa" would win the stable tiebreak, so this proves provenance is
        // consulted before the tiebreak rather than after it.
        Assert.AreEqual("zzz-retail", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void AnUnknownAudiobookContainerRanksLastButIsNotDropped()
    {
        // The whole point of the audiobook fix: a release-name-only source
        // frequently cannot state a container, and dropping those candidates
        // reported "nothing found" for a search that had usable results.
        var ranked = ExternalCandidateRanker.Rank(
            [
                Candidate("no-container", format: null, mediaType: RequestMediaType.Audiobook),
                Candidate("m4b", format: "m4b", mediaType: RequestMediaType.Audiobook)
            ],
            RequestMediaType.Audiobook);

        Assert.AreEqual(2, ranked.Count);
        Assert.AreEqual("m4b", ranked[0].ProviderResultId);
        Assert.AreEqual("no-container", ranked[1].ProviderResultId);
    }

    [TestMethod]
    public void TheOrderIsStableRegardlessOfTheProvidersOwnResponseOrder()
    {
        // A retry loop that advances through candidates needs the same order
        // every time it looks, and a provider may reorder its response.
        var forward = ExternalCandidateRanker.Rank(
            [Candidate("c"), Candidate("a"), Candidate("b")], RequestMediaType.Ebook);
        var reversed = ExternalCandidateRanker.Rank(
            [Candidate("b"), Candidate("a"), Candidate("c")], RequestMediaType.Ebook);

        Assert.AreEqual("a", forward[0].ProviderResultId);
        CollectionAssert.AreEqual(
            forward.Select(option => option.ProviderResultId).ToArray(),
            reversed.Select(option => option.ProviderResultId).ToArray());
    }

    [TestMethod]
    public void ManyEquallyAcceptableCandidatesStillProduceExactlyOneWinner()
    {
        // The dealership case: thirty acceptable copies is not ambiguity.
        var candidates = Enumerable.Range(0, 30)
            .Select(index => Candidate($"c{index:D2}"))
            .ToArray();

        var best = ExternalCandidateRanker.SelectBest(candidates, RequestMediaType.Ebook);

        Assert.IsNotNull(best);
        Assert.AreEqual("c00", best.ProviderResultId);
    }

    [TestMethod]
    public void AnEmptySetHasNoWinner()
    {
        Assert.IsNull(ExternalCandidateRanker.SelectBest([], RequestMediaType.Ebook));
    }

    [TestMethod]
    public void ACreditedHumanNarratorWinsAnOtherwiseExactAudiobookTie()
    {
        // The single confirmed candidate is chosen before
        // AudiobookCandidateSelector ever sees the requester's preference, so
        // without this dimension a reader credited in the release name would
        // lose to an unknown-narration record on an ID comparison alone.
        var ranked = ExternalCandidateRanker.Rank(
            [
                Candidate("aaa-unknown", format: null, mediaType: RequestMediaType.Audiobook),
                Candidate(
                    "zzz-human", format: null, mediaType: RequestMediaType.Audiobook,
                    narrationKind: NarrationKind.Human)
            ],
            RequestMediaType.Audiobook);

        Assert.AreEqual("zzz-human", ranked[0].ProviderResultId);
    }

    [TestMethod]
    public void AUsableContainerStillOutranksACreditedNarrator()
    {
        // Narration is a late tiebreak, not a reason to prefer a container FL
        // cannot use.
        var ranked = ExternalCandidateRanker.Rank(
            [
                Candidate(
                    "unknown-container-human", format: null, mediaType: RequestMediaType.Audiobook,
                    narrationKind: NarrationKind.Human),
                Candidate("m4b-unknown-narration", format: "m4b", mediaType: RequestMediaType.Audiobook)
            ],
            RequestMediaType.Audiobook);

        Assert.AreEqual("m4b-unknown-narration", ranked[0].ProviderResultId);
    }
}
