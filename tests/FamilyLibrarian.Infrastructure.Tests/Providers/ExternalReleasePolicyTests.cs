using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ExternalReleasePolicyTests
{
    [TestMethod]
    public void NoReleaseEvidenceIsAcceptable()
    {
        var verdict = ExternalReleasePolicy.Evaluate(release: null, RequestMediaType.Ebook);

        Assert.IsFalse(verdict.RequiresConfirmation);
        Assert.IsNull(verdict.Reason);
    }

    [TestMethod]
    public void AnOrdinarySingleTitleReleaseIsAcceptable()
    {
        var release = new ExternalProviderReleaseEvidence(
            "Debt.of.Honor.RETAIL.EPUB", "epub", 1_452_821, IsCollection: false, PartCount: 1,
            IsSample: false, IsAbridged: null, IsUnabridged: null, QualityTags: ["retail"], AgeDays: null);

        var verdict = ExternalReleasePolicy.Evaluate(release, RequestMediaType.Ebook);

        Assert.IsFalse(verdict.RequiresConfirmation);
    }

    [TestMethod]
    public void ACollectionRequiresConfirmation()
    {
        var release = new ExternalProviderReleaseEvidence(
            "Jack.Ryan.Omnibus", "epub", null, IsCollection: true, PartCount: 12,
            IsSample: false, IsAbridged: null, IsUnabridged: null, QualityTags: [], AgeDays: null);

        var verdict = ExternalReleasePolicy.Evaluate(release, RequestMediaType.Ebook);

        Assert.IsTrue(verdict.RequiresConfirmation);
        Assert.Contains("collection", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ASampleRequiresConfirmation()
    {
        var release = new ExternalProviderReleaseEvidence(
            "Preview", "epub", null, IsCollection: false, PartCount: 1,
            IsSample: true, IsAbridged: null, IsUnabridged: null, QualityTags: [], AgeDays: null);

        var verdict = ExternalReleasePolicy.Evaluate(release, RequestMediaType.Ebook);

        Assert.IsTrue(verdict.RequiresConfirmation);
    }

    [TestMethod]
    public void AnAbridgedAudiobookWithNoUnabridgedFlagRequiresConfirmation()
    {
        var release = new ExternalProviderReleaseEvidence(
            "Book (Abridged)", "m4b", null, IsCollection: false, PartCount: 1,
            IsSample: false, IsAbridged: true, IsUnabridged: false, QualityTags: [], AgeDays: null);

        var verdict = ExternalReleasePolicy.Evaluate(release, RequestMediaType.Audiobook);

        Assert.IsTrue(verdict.RequiresConfirmation);
    }

    [TestMethod]
    public void AnAbridgedEbookIsNotFlagged()
    {
        // Nothing downstream distinguishes abridged/unabridged ebook
        // editions yet -- the policy deliberately only acts on this for
        // Audiobook requests.
        var release = new ExternalProviderReleaseEvidence(
            "Book (Abridged)", "epub", null, IsCollection: false, PartCount: 1,
            IsSample: false, IsAbridged: true, IsUnabridged: false, QualityTags: [], AgeDays: null);

        var verdict = ExternalReleasePolicy.Evaluate(release, RequestMediaType.Ebook);

        Assert.IsFalse(verdict.RequiresConfirmation);
    }
}
