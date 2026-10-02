using FamilyLibrarian.Web.Acquisition;
using FamilyLibrarian.Web.Client.Theme;
using MudBlazor;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// What each provider-activity outcome looks like, and that a step the system
/// is handling on its own is never drawn like an error. Both the request page
/// and the Tasks dashboard take this from <see cref="MediaTypeVisuals"/>, so
/// these tests are the single place the two can be held in agreement.
/// </summary>
[TestClass]
public sealed class ProviderAttemptPresentationTests
{
    private static readonly string[] AllOutcomes =
        ["NoMatch", "CandidatesFound", "Acquired", "Submitted", "Retrying", "Failed", "Blocked"];

    [TestMethod]
    [DataRow("Submitted")]
    [DataRow("Retrying")]
    public void AStepTheSystemIsHandlingItselfIsBlueNotRed(string outcome)
    {
        // "Submitted" used to fall through to the default and render red.
        Assert.AreEqual(Color.Info, MediaTypeVisuals.AttemptOutcomeColor(outcome));
    }

    [TestMethod]
    public void OnlyARealFailureIsRed()
    {
        var red = AllOutcomes
            .Where(outcome => MediaTypeVisuals.AttemptOutcomeColor(outcome) == Color.Error)
            .ToArray();

        Assert.HasCount(1, red);
        Assert.AreEqual("Failed", red[0]);
    }

    [TestMethod]
    public void TheOtherOutcomesKeepTheirMeaning()
    {
        Assert.AreEqual(Color.Success, MediaTypeVisuals.AttemptOutcomeColor("Acquired"));
        Assert.AreEqual(Color.Warning, MediaTypeVisuals.AttemptOutcomeColor("Blocked"));
        Assert.AreEqual(Color.Info, MediaTypeVisuals.AttemptOutcomeColor("CandidatesFound"));
        Assert.AreEqual(Color.Default, MediaTypeVisuals.AttemptOutcomeColor("NoMatch"));
    }

    [TestMethod]
    public void EveryOutcomeHasAPlainLabelHintAndIcon()
    {
        foreach (var outcome in AllOutcomes)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(MediaTypeVisuals.AttemptOutcomeLabel(outcome)));
            Assert.IsFalse(string.IsNullOrWhiteSpace(MediaTypeVisuals.AttemptOutcomeHint(outcome)));
            Assert.IsFalse(string.IsNullOrWhiteSpace(MediaTypeVisuals.AttemptOutcomeIcon(outcome)));
        }
    }

    [TestMethod]
    public void ConfusingEnumNamesReadAsPlainLanguage()
    {
        Assert.AreEqual("In progress", MediaTypeVisuals.AttemptOutcomeLabel("Submitted"));
        Assert.AreEqual("Trying next copy", MediaTypeVisuals.AttemptOutcomeLabel("Retrying"));
        Assert.AreEqual("Nothing found", MediaTypeVisuals.AttemptOutcomeLabel("NoMatch"));
        Assert.AreEqual("Candidates found", MediaTypeVisuals.AttemptOutcomeLabel("CandidatesFound"));
    }

    [TestMethod]
    public void TheHintSaysWhetherAPersonNeedsToAct()
    {
        Assert.Contains("Nothing needs doing", MediaTypeVisuals.AttemptOutcomeHint("Submitted"));
        Assert.Contains("Nothing needs doing", MediaTypeVisuals.AttemptOutcomeHint("Retrying"));
        Assert.Contains("needs to look", MediaTypeVisuals.AttemptOutcomeHint("Failed"));
    }

    [TestMethod]
    public void AnUnknownFutureOutcomeDegradesToAReadableNeutralChip()
    {
        Assert.AreEqual(Color.Default, MediaTypeVisuals.AttemptOutcomeColor("SomethingNew"));
        Assert.AreEqual("SomethingNew", MediaTypeVisuals.AttemptOutcomeLabel("SomethingNew"));
    }

    [TestMethod]
    public void TheFormatChipForAnAdvancingJobIsBlueNotRed()
    {
        Assert.AreEqual(Color.Info, MediaTypeVisuals.ProgressColor("AcquisitionRetrying"));
        Assert.AreEqual(Color.Error, MediaTypeVisuals.ProgressColor("AcquisitionFailed"));
    }

    [TestMethod]
    public async Task ASignalWakesTheWorkerImmediatelyInsteadOfAtTheNextSweep()
    {
        using var signal = new AutomaticFulfillmentSignal();

        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.IsFalse(waiting.IsCompleted, "Nothing has asked for a pass yet.");

        signal.Request();

        Assert.IsTrue(await waiting.WaitAsync(TimeSpan.FromSeconds(5)), "A request must end the wait early.");
    }

    [TestMethod]
    public async Task WithNoSignalTheWorkerStillWakesAtItsInterval()
    {
        using var signal = new AutomaticFulfillmentSignal();

        var woken = await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.IsFalse(woken, "Timing out is the normal periodic sweep, not a signal.");
    }

    [TestMethod]
    public async Task ManyRequestsWhileBusyCollapseIntoOneFurtherPass()
    {
        using var signal = new AutomaticFulfillmentSignal();

        signal.Request();
        signal.Request();
        signal.Request();

        Assert.IsTrue(await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.IsFalse(
            await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None),
            "Repeated requests are one wake-up, not a backlog of passes.");
    }
}
