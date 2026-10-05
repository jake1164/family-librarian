using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

/// <summary>
/// PROVIDER-7: a copy that failed must not be fetched again just because the
/// same release turns up under another provider reference, and doing so must
/// not spend one of the provider's limited attempts.
/// </summary>
[TestClass]
public sealed class ExternalReleaseFingerprintAndExclusionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static FulfillmentOption Option(string resultId, string? releaseName, long? sizeBytes) =>
        new(
            ProviderId: "prowlarr",
            ProviderResultId: resultId,
            WorkId: Guid.Empty,
            EditionId: null,
            MediaType: RequestMediaType.Audiobook,
            OptionKind: OptionKind.DirectAcquisition,
            AcquisitionMethod: AcquisitionMethod.DirectDownload,
            Format: null, Language: null, Quality: null, Availability: null, Cost: 0m, Currency: null,
            LicenseOrUsageStatus: null, DrmStatus: "unknown", ExternalActionUri: null, ProviderData: resultId,
            SizeBytes: sizeBytes,
            ReleaseName: releaseName);

    private static BookRequest NewRequest(out Guid formatId)
    {
        var request = new BookRequest(Guid.NewGuid(), Guid.NewGuid(), [RequestMediaType.Audiobook], null, Now);
        formatId = request.Formats.Single().Id;
        return request;
    }

    // ------------------------------------------------------------------ fingerprint

    [TestMethod]
    public void TheSameReleaseSpelledWithDifferentPunctuationAndTheSameSizeIsOneRelease()
    {
        var dotted = ExternalReleaseFingerprint.Compute("Ray.Bradbury-Fahrenheit.451", 269_285_000);
        var spaced = ExternalReleaseFingerprint.Compute("Ray Bradbury - Fahrenheit 451", 269_285_000);

        Assert.IsNotNull(dotted);
        Assert.AreEqual(dotted, spaced);
    }

    [TestMethod]
    public void ADifferentSizeIsADifferentRelease()
    {
        // Same title, different bytes: a different edition or recording, not a
        // re-posting, so it must stay eligible.
        Assert.AreNotEqual(
            ExternalReleaseFingerprint.Compute("Ray.Bradbury-Fahrenheit.451", 269_285_000),
            ExternalReleaseFingerprint.Compute("Ray.Bradbury-Fahrenheit.451", 269_285_001));
    }

    [TestMethod]
    public void WithoutBothANameAndASizeNothingIsCalledEquivalent()
    {
        Assert.IsNull(ExternalReleaseFingerprint.Compute(null, 269_285_000));
        Assert.IsNull(ExternalReleaseFingerprint.Compute("   ", 269_285_000));
        Assert.IsNull(ExternalReleaseFingerprint.Compute("Ray.Bradbury-Fahrenheit.451", null));
        Assert.IsNull(ExternalReleaseFingerprint.Compute("Ray.Bradbury-Fahrenheit.451", 0));
        Assert.IsNull(ExternalReleaseFingerprint.Compute("---", 1_000));
    }

    // ------------------------------------------------------------------ exclusions

    [TestMethod]
    public void ARepostingOfAFailedReleaseIsExcludedEvenUnderADifferentReference()
    {
        var request = NewRequest(out var formatId);
        var failed = Option("c_first", "Ray.Bradbury-Fahrenheit.451", 269_285_000);
        request.RecordAutomaticCandidateFailure(
            formatId, "prowlarr", failed.ProviderResultId, "Could not repair.", Now,
            ExternalReleaseFingerprint.Compute(failed.ReleaseName, failed.SizeBytes));

        var exclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates, "prowlarr", formatId);

        Assert.IsTrue(exclusions.Excludes(failed));
        Assert.IsTrue(
            exclusions.Excludes(Option("c_twin", "Ray Bradbury - Fahrenheit 451", 269_285_000)),
            "The same release under another reference must not be tried again.");
        Assert.IsFalse(
            exclusions.Excludes(Option("c_other", "Ray.Bradbury-Fahrenheit.451", 41_062_236)),
            "A different size is a different release and must stay eligible.");
        Assert.IsFalse(exclusions.Excludes(Option("c_unrelated", "Fahrenheit 451 by Ray Bradbury EPUB", 9_552_527)));
    }

    [TestMethod]
    public void ARequestersKeepLookingExcludesThatRecordButNotItsLookalikes()
    {
        // A requester setting an edition aside says nothing about whether other
        // records are the same bytes, so only automatic failures contribute a
        // release fingerprint.
        var request = NewRequest(out var formatId);
        request.MarkNeedsReview(
            RequestReviewCategory.PreferenceAmbiguity, "Two editions.", Now,
            [new RequestReviewCandidateInput(formatId, "prowlarr", "c_declined", "Fahrenheit 451", "Ray Bradbury", "en", null, null, null, false)]);
        request.DismissReviewPreference(null, Now);

        var exclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates, "prowlarr", formatId);

        Assert.IsTrue(exclusions.Excludes(Option("c_declined", "Ray.Bradbury-Fahrenheit.451", 269_285_000)));
        Assert.IsFalse(exclusions.Excludes(Option("c_lookalike", "Ray.Bradbury-Fahrenheit.451", 269_285_000)));
        Assert.IsEmpty(exclusions.FailedReleaseFingerprints);
    }

    [TestMethod]
    public void TheCandidateBeingFetchedIsNeverExcludedByItsOwnEarlierEntry()
    {
        var request = NewRequest(out var formatId);
        request.RecordAutomaticCandidateFailure(formatId, "prowlarr", "c_retry", "Timed out.", Now);

        var exclusions = ExternalCandidateExclusions.From(
            request.DeclinedCandidates, "prowlarr", formatId, exceptResultId: "c_retry");

        Assert.IsFalse(exclusions.Excludes(Option("c_retry", null, null)));
    }

    [TestMethod]
    public void AnotherProvidersOrAnotherFormatsFailuresDoNotLeakIn()
    {
        var request = new BookRequest(
            Guid.NewGuid(), Guid.NewGuid(), [RequestMediaType.Ebook, RequestMediaType.Audiobook], null, Now);
        var ebookFormat = request.Formats.Single(format => format.MediaType == RequestMediaType.Ebook).Id;
        var audioFormat = request.Formats.Single(format => format.MediaType == RequestMediaType.Audiobook).Id;
        request.RecordAutomaticCandidateFailure(ebookFormat, "prowlarr", "c_ebook", "Failed.", Now);
        request.RecordAutomaticCandidateFailure(audioFormat, "other-source", "c_other", "Failed.", Now);

        var exclusions = ExternalCandidateExclusions.From(request.DeclinedCandidates, "prowlarr", audioFormat);

        Assert.IsFalse(exclusions.Excludes(Option("c_ebook", null, null)));
        Assert.IsFalse(exclusions.Excludes(Option("c_other", null, null)));
    }

    [TestMethod]
    public void NoExclusionsExcludesNothing()
    {
        Assert.IsFalse(ExternalCandidateExclusions.None.Excludes(
            Option("c_any", "Ray.Bradbury-Fahrenheit.451", 269_285_000)));
    }
}
