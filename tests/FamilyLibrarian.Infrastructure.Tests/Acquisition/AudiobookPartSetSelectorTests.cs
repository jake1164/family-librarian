using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class AudiobookPartSetSelectorTests
{
    private static readonly string[] OrderedMembers = ["part-1", "part-2"];

    [TestMethod]
    public void ACompleteSetOfFragmentsIsSelectedInPartOrder()
    {
        var selection = AudiobookPartSetSelector.TrySelect([Part(2, 2), Part(1, 2)]);

        Assert.IsNotNull(selection);
        Assert.AreEqual(2, selection.Total);
        CollectionAssert.AreEqual(OrderedMembers, selection.MemberResultIds.ToArray());
        Assert.AreEqual("example-source", selection.ProviderId);
    }

    [TestMethod]
    public void ASetWithAMissingPartIsNotSelected() =>
        Assert.IsNull(AudiobookPartSetSelector.TrySelect([Part(1, 3), Part(3, 3)]));

    [TestMethod]
    public void ALonePartIsNotASet() =>
        Assert.IsNull(AudiobookPartSetSelector.TrySelect([Part(1, 2)]));

    [TestMethod]
    public void ADuplicateCopyOfAPartMakesTheSetAmbiguous()
    {
        var duplicate = Part(2, 2) with { ProviderResultId = "other-part-2" };

        Assert.IsNull(AudiobookPartSetSelector.TrySelect([Part(1, 2), Part(2, 2), duplicate]));
    }

    [TestMethod]
    public void PartsFromDifferentProvidersAreNotCombined()
    {
        var foreign = Part(2, 2) with { ProviderId = "another-source" };

        Assert.IsNull(AudiobookPartSetSelector.TrySelect([Part(1, 2), foreign]));
    }

    [TestMethod]
    public void AnyConcernBeyondBeingAFragmentDisqualifiesTheSet()
    {
        var concerning = new Func<FulfillmentOption, FulfillmentOption>[]
        {
            option => option with { FragmentOnlyConcern = false },
            option => option with { RequiresLanguageConfirmation = true },
            option => option with { HasPlausibleTitle = false },
            option => option with { MatchBasis = BookMatchBasis.TitleAuthor },
            option => option with { MatchBasis = null },
            option => option with { AuthorAffinity = AuthorAffinity.Evaluate("Rebecca Yarros", "Stephen King") },
            option => option with { Format = "wav" },
            option => option with { DrmStatus = "encrypted" },
            option => option with { SizeBytes = 0 },
            option => option with { IsAbridged = true, IsUnabridged = false }
        };

        for (var index = 0; index < concerning.Length; index++)
            Assert.IsNull(
                AudiobookPartSetSelector.TrySelect([Part(1, 2), concerning[index](Part(2, 2))]),
                $"variant {index}: a set containing a member with a further concern must not be automatic");
    }

    [TestMethod]
    public void AnUnreportedContainerIsAllowedLikeEverywhereElseInAutomaticAudiobookSelection() =>
        Assert.IsNotNull(AudiobookPartSetSelector.TrySelect([Part(1, 2) with { Format = null }, Part(2, 2) with { Format = null }]));

    [TestMethod]
    public void ASetLargerThanTheBoundIsNotSelected()
    {
        var total = AudiobookPartSetSelector.MaxParts + 1;
        var parts = Enumerable.Range(1, total).Select(number => Part(number, total)).ToArray();

        Assert.IsNull(AudiobookPartSetSelector.TrySelect(parts));
    }

    [TestMethod]
    public void TwoDifferentCompleteSetsAreAChoiceNotSomethingToResolveUnattended()
    {
        var setOfThree = Enumerable.Range(1, 3).Select(number => Part(number, 3)).ToArray();

        Assert.IsNull(AudiobookPartSetSelector.TrySelect([Part(1, 2), Part(2, 2), .. setOfThree]));
    }

    [TestMethod]
    public void HasSameMembersComparesTheResultIdsRegardlessOfOrder()
    {
        var selection = AudiobookPartSetSelector.TrySelect([Part(1, 2), Part(2, 2)])!;

        Assert.IsTrue(selection.HasSameMembers(["part-2", "part-1"]));
        Assert.IsFalse(selection.HasSameMembers(["part-1"]));
        Assert.IsFalse(selection.HasSameMembers(["part-1", "part-2", "part-3"]));
        Assert.IsFalse(selection.HasSameMembers(["part-1", "different"]));
    }

    private static FulfillmentOption Part(int number, int total)
    {
        var name = $"The.Empyrean.[03].Onyx.Storm.{number}.of.{total}";
        return new(
            "example-source", $"part-{number}", Guid.Empty, null, RequestMediaType.Audiobook, OptionKind.DirectAcquisition,
            AcquisitionMethod.DirectDownload, "m4b", null, null, null, 0m, null, null, null, null, null,
            MatchBasis: BookMatchBasis.StrictTitle,
            SizeBytes: 500_000_000,
            ReleaseName: name,
            HasPlausibleTitle: true,
            AudiobookPart: ExternalAudiobookPartEvidence.Parse(name),
            FragmentOnlyConcern: true);
    }
}
