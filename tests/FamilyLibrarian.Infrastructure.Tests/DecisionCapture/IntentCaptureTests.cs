using System.Text.Json;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-b, intent: chat messages are saved only under their own switch, and only the message and previous search are kept.</summary>
[TestClass]
public sealed class IntentCaptureTests
{
    private const string Find = "{\"schema\":\"book-intent/1\",\"intent\":\"find_book\",\"title\":\"The Lantern Road\",\"author\":null}";
    private static readonly string[] ExpectedProperties = ["message", "previousSearch", "schemaVersion"];

    private static CaptureRig Rig(bool decisions = true, bool chat = true) =>
        new(ModelRole.IntentInterpretation, (_, _) => ScriptedModel.Ok(Find, model: "provider/intent-4b"), ModelReasoningMode.Disabled, decisions, chat);

    private static Task<BookIntentInterpretation?> Run(CaptureRig rig, IDecisionCaptureSink? sink, Guid user, string text = "  the  lantern road please ",
        string? previous = "Lantern Cycle by Mara Quill") =>
        new ModelBookIntentInterpreter(rig.Policy, sink).InterpretAsync(user, text, previous, CancellationToken.None);

    [TestMethod]
    public async Task AChatMessageIsSavedUnderTheChatSwitchAndReproducesThePromptExactly()
    {
        var rig = Rig();
        var user = Guid.NewGuid();

        var result = await Run(rig, rig.Sink, user);

        Assert.AreEqual(BookIntent.FindBook, result?.Intent);
        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelRole.IntentInterpretation, write.Snapshot.Role);
        Assert.AreEqual("intent:" + user.ToString("N"), write.Snapshot.SubjectReference);
        Assert.AreEqual(user, write.Snapshot.UserId, "Deleting the person deletes their saved messages.");
        Assert.IsNull(write.Snapshot.RequestId);
        Assert.IsNull(write.Snapshot.StaleReason);
        Assert.AreEqual("find_book", write.Call.Answer, "Only the intent is kept, never the title or author the model guessed.");
        Assert.AreEqual("provider/intent-4b", write.Call.ReportedModel);
        var rebuilt = TuningCaseRenderer.Render(ModelRole.IntentInterpretation,
            TuningInputJson.Read(ModelRole.IntentInterpretation, write.Snapshot.SnapshotJson), TuningSource.Household, TuningCoverage.ContractOnly);
        var sent = rig.Model.Calls.Single().Request.Messages;
        for (var i = 0; i < sent.Count; i++) Assert.AreEqual(sent[i].Content, rebuilt.Messages[i].Content, "Round trip reproduces the prompt byte for byte.");
    }

    [TestMethod]
    public async Task OnlyTheMessageAndPreviousSearchAreKeptNothingAboutThePerson()
    {
        var rig = Rig();
        var user = Guid.NewGuid();

        await Run(rig, rig.Sink, user);

        using var document = JsonDocument.Parse(rig.Writer.Writes.Single().Snapshot.SnapshotJson);
        CollectionAssert.AreEquivalent(ExpectedProperties, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.IsFalse(rig.Writer.Writes.Single().Snapshot.SnapshotJson.Contains(user.ToString("N"), StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("the lantern road please", document.RootElement.GetProperty("message").GetString(), "Saved as normalized, exactly as it was rendered.");
    }

    [TestMethod]
    public async Task ChatCaptureOffTheDefaultSavesNothingEvenWithDecisionCaptureOn()
    {
        var rig = Rig(decisions: true, chat: false);

        var result = await Run(rig, rig.Sink, Guid.NewGuid());

        Assert.AreEqual(BookIntent.FindBook, result?.Intent);
        Assert.IsEmpty(rig.Writer.Writes);
        Assert.AreEqual(1, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task TurningDecisionCaptureOffStopsChatCaptureToo()
    {
        var rig = Rig(decisions: false, chat: true);

        await Run(rig, rig.Sink, Guid.NewGuid());

        Assert.IsEmpty(rig.Writer.Writes);
    }

    [TestMethod]
    public async Task ASinkThatThrowsDoesNotChangeHowTheMessageIsRead()
    {
        var baselineRig = Rig(chat: false);
        var baseline = await Run(baselineRig, null, Guid.NewGuid());
        var rig = Rig();
        var sink = new ThrowingSink();

        var result = await Run(rig, sink, Guid.NewGuid());

        Assert.AreEqual(1, sink.Calls);
        Assert.AreEqual(baseline?.Intent, result?.Intent);
        Assert.AreEqual(baseline?.Title, result?.Title);
        Assert.AreEqual(baselineRig.Model.Calls.Count, rig.Model.Calls.Count);
        Assert.AreEqual(baselineRig.Ledger.Records.Count, rig.Ledger.Records.Count);
    }

    [TestMethod]
    public async Task AnEmptyMessageMakesNoCallAndSavesNothing()
    {
        var rig = Rig();

        var result = await Run(rig, rig.Sink, Guid.NewGuid(), text: "   ");

        Assert.IsNull(result);
        Assert.IsEmpty(rig.Model.Calls);
        Assert.IsEmpty(rig.Writer.Writes);
    }

    [TestMethod]
    public async Task ARejectedReplyIsStillKeptWithItsOutcomeAndNoAnswer()
    {
        var rig = new CaptureRig(ModelRole.IntentInterpretation, (_, _) => ScriptedModel.Ok("not json"), ModelReasoningMode.Disabled, true, true);

        var result = await Run(rig, rig.Sink, Guid.NewGuid());

        Assert.IsNull(result);
        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelCallOutcome.InvalidJson, write.Call.Outcome);
        Assert.IsNull(write.Call.Answer);
    }
}
