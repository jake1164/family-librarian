using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

/// <summary>
/// The provider-activity ledger is the first thing an administrator reads, so
/// each line must say which attempt this is, whether anything needs doing, and
/// what happens next -- and must never present a step the system is already
/// handling as though it were a fault.
/// </summary>
[TestClass]
public sealed class AutomaticAttemptNarrativeTests
{
    [TestMethod]
    public void AnAdvancingStepSaysWhichAttemptItIsAndThatNothingNeedsDoing()
    {
        var text = AutomaticAttemptNarrative.Advancing("SABnzbd could not repair this release.", 2, 3);

        Assert.Contains("Attempt 2 of 3", text);
        Assert.Contains("SABnzbd could not repair this release.", text);
        Assert.Contains("Nothing needs doing", text);
        Assert.Contains("next best copy", text);
    }

    [TestMethod]
    public void AnExhaustedBudgetSaysAPersonIsNeeded()
    {
        var text = AutomaticAttemptNarrative.Exhausted("The file was not the requested book.", 3, 3);

        Assert.Contains("Attempt 3 of 3", text);
        Assert.Contains("librarian needs to choose a source", text);
        Assert.DoesNotContain("Nothing needs doing", text);
    }

    [TestMethod]
    public void AReasonWithoutAFullStopStillReadsAsASentence()
    {
        var text = AutomaticAttemptNarrative.Advancing("The source could not deliver this copy", 1, 3);

        Assert.Contains("deliver this copy. Nothing needs doing", text);
    }

    [TestMethod]
    public void ABlankReasonStillProducesAnExplicitStatement()
    {
        Assert.Contains("no reason was given.", AutomaticAttemptNarrative.Advancing("  ", 1, 3));
    }

    [TestMethod]
    public void TheFirstAttemptIsNotLabelledWithACounter()
    {
        var first = AutomaticAttemptNarrative.Starting("Fahrenheit 451 (MP3, 269.3 MB)", 1, 3);
        var later = AutomaticAttemptNarrative.Starting("Fahrenheit 451 (MP3, 269.3 MB)", 2, 3);

        Assert.DoesNotContain("Attempt", first);
        Assert.Contains("Release: Fahrenheit 451 (MP3, 269.3 MB)", first);
        Assert.Contains("Attempt 2 of 3", later);
    }

    [TestMethod]
    public void AnUnknownCandidateStillProducesALine()
    {
        Assert.AreEqual("Fetching a copy automatically.", AutomaticAttemptNarrative.Starting(null, 1, 3));
    }

    [TestMethod]
    public void ACandidateIsDescribedByReleaseNameFormatAndSize()
    {
        var label = AutomaticAttemptNarrative.DescribeCandidate(
            "Ray.Bradbury-Fahrenheit.451", null, "mp3", 269_285_000);

        Assert.AreEqual("Ray.Bradbury-Fahrenheit.451 (MP3, 269.3 MB)", label);
    }

    [TestMethod]
    public void AWorkTitleIsUsedWhenTheProviderGaveNoReleaseName()
    {
        Assert.AreEqual(
            "Fahrenheit 451",
            AutomaticAttemptNarrative.DescribeCandidate(null, "Fahrenheit 451", null, null));
    }

    [TestMethod]
    public void NothingNameableProducesNoLabel()
    {
        Assert.IsNull(AutomaticAttemptNarrative.DescribeCandidate(null, null, "epub", 1_000));
        Assert.IsNull(AutomaticAttemptNarrative.DescribeCandidate("   ", "  ", null, null));
    }

    [TestMethod]
    public void AnUntrustedReleaseNameIsCleanedAndBounded()
    {
        // The name is provider-supplied. Control characters and runs of
        // whitespace are removed and the length is capped so one hostile or
        // sloppy name cannot swamp the ledger.
        var hostile = "Evil\r\n\tName\u0007" + new string('x', 400);

        var label = AutomaticAttemptNarrative.DescribeCandidate(hostile, null, "epub", 5_000_000)!;

        Assert.DoesNotContain("\r", label);
        Assert.DoesNotContain("\n", label);
        Assert.DoesNotContain("\u0007", label);
        Assert.StartsWith("Evil Name", label);
        Assert.IsLessThan(160, label.Length);
        Assert.Contains("5 MB", label);
    }
}
