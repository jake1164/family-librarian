using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.DecisionCapture;

/// <summary>C1-b, search guidance: the decision is saved after it is final, reproduces the prompt exactly, and never changes the result.</summary>
[TestClass]
public sealed class SearchGuidanceCaptureTests
{
    private const string Good = "{\"schema\":\"search-guidance/1\",\"give_up\":false,\"suggestions\":[{\"reason\":\"alternate_published_title\",\"value\":\"A New Journey\",\"evidence_ids\":[\"req-title\"],\"note\":\"Try a new title.\"}]}";
    private static readonly BookIdentity Requested = CaptureData.Requested;
    private static readonly string[] ExpectedKeys = ["c1", "c2", "c3"];

    private static CaptureRig Rig(string reply = Good, ModelReasoningMode reasoning = ModelReasoningMode.Low, bool capture = true, int maxOutput = 1024) =>
        new(ModelRole.SearchGuidance, (_, _) => ScriptedModel.Ok(reply, model: "provider/search-7b"), reasoning, capture, maxOutput: maxOutput);

    private static SearchGuidanceService Service(CaptureRig rig, IDecisionCaptureSink? sink, FakeFindingStore? findings = null) =>
        new(rig.Resolver, rig.Policy, findings ?? new FakeFindingStore(), rig.Clock, null, sink);

    private static Task<SearchGuidance?> Run(SearchGuidanceService service, Guid? format = null, Guid? request = null,
        IReadOnlyCollection<string>? tried = null) =>
        service.GetAsync(request ?? Guid.NewGuid(), format ?? Guid.NewGuid(), Requested, CaptureData.SearchResults(Requested), tried ?? ["Lantern Road"],
            CaptureRig.Now, CancellationToken.None);

    [TestMethod]
    public async Task ASuccessfulCallSavesOneSnapshotThatReproducesThePromptExactlyAndOneCallWithItsRunIdentity()
    {
        var rig = Rig();
        var findings = new FakeFindingStore();
        var request = Guid.NewGuid();
        var format = Guid.NewGuid();

        var proposal = await Run(Service(rig, rig.Sink, findings), format, request);

        Assert.IsNotNull(proposal);
        var write = rig.Writer.Writes.Single();
        var snapshot = write.Snapshot;
        Assert.AreEqual(ModelRole.SearchGuidance, snapshot.Role);
        Assert.AreEqual(format.ToString("N"), snapshot.SubjectReference, "Same convention as the finding's subject.");
        Assert.AreEqual(request, snapshot.RequestId);
        Assert.IsNull(snapshot.StaleReason, "The rebuilt prompt hashes to exactly what was sent.");
        Assert.IsFalse(snapshot.ExcerptsInvolved);
        var call = write.Call;
        Assert.AreEqual("local/alias", call.ModelAlias);
        Assert.AreEqual("provider/search-7b", call.ReportedModel);
        Assert.AreEqual(ModelReasoningMode.Low, call.Reasoning);
        Assert.AreEqual(ModelPlacement.Local, call.Placement);
        Assert.AreEqual(ModelCallOutcome.Succeeded, call.Outcome);
        Assert.AreEqual("Search", call.Answer);
        Assert.AreEqual(findings.Findings.Single().Id, call.SemanticFindingId);
        Assert.AreEqual(rig.Ledger.Records.Single().Id, call.ModelUsageRecordId);
        Assert.AreEqual(rig.Settings.Id, call.SettingsId);
        var sent = rig.Model.Calls.Single().Request;
        Assert.AreEqual(RenderedMessagesIdentity.Compute(sent.Messages, sent.JsonObjectOutput, sent.MaxOutputTokens), call.RenderedMessagesHash);
    }

    [TestMethod]
    public async Task TheSnapshotRebuildsIntoTheExactMessagesThatWereSent()
    {
        var rig = Rig();
        await Run(Service(rig, rig.Sink));
        var stored = rig.Writer.Writes.Single().Snapshot;

        var rebuilt = TuningCaseRenderer.Render(ModelRole.SearchGuidance, TuningInputJson.Read(ModelRole.SearchGuidance, stored.SnapshotJson),
            TuningSource.Household, TuningCoverage.ContractOnly);

        var sent = rig.Model.Calls.Single().Request.Messages;
        Assert.HasCount(sent.Count, rebuilt.Messages);
        for (var i = 0; i < sent.Count; i++)
        {
            Assert.AreEqual(sent[i].Role, rebuilt.Messages[i].Role);
            Assert.AreEqual(sent[i].Content, rebuilt.Messages[i].Content, "Round trip: snapshot -> production renderer reproduces the prompt byte for byte.");
        }
    }

    [TestMethod]
    public async Task TheSnapshotHoldsNoProviderIdsTokensRevisionsLinksOrFileFieldsAndTheLinksCarryTheProviderIds()
    {
        var rig = Rig();
        await Run(Service(rig, rig.Sink));
        var write = rig.Writer.Writes.Single();
        var json = write.Snapshot.SnapshotJson;

        foreach (var forbidden in new[]
        {
            CaptureData.SecretToken, CaptureData.SecretRevision, "provider-data-SECRET", "example-source", "result-1", "provider-ref-", "https://",
            "example.test", "origin summary", "asin", "B000TEST", "providerId", "acquireToken", "candidateRevision"
        })
            Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Snapshot JSON must not contain '{forbidden}'.");
        Assert.HasCount(3, write.Links);
        CollectionAssert.AreEqual(ExpectedKeys, write.Links.Select(link => link.CandidateKey).ToArray());
        Assert.AreEqual("result-2", write.Links[1].ProviderResultId);
        Assert.IsTrue(write.Links.All(link => link.ProviderId == "example-source"));
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual("c1", document.RootElement.GetProperty("results")[0].GetProperty("candidate").GetProperty("key").GetString());
    }

    [TestMethod]
    public async Task ARejectedReplyIsStillADecisionWorthKeepingWithItsOutcomeAndNoAnswer()
    {
        var rig = Rig(reply: "not json at all");

        var proposal = await Run(Service(rig, rig.Sink));

        Assert.IsNull(proposal);
        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(ModelCallOutcome.InvalidJson, write.Call.Outcome);
        Assert.IsNull(write.Call.Answer);
        Assert.IsNull(write.Snapshot.StaleReason);
    }

    [TestMethod]
    public async Task TheClampedOutputCapIsWhatTheSnapshotIsVerifiedAgainstSoALowLimitStillRoundTrips()
    {
        var rig = Rig(maxOutput: 300);

        await Run(Service(rig, rig.Sink));

        Assert.AreEqual(300, rig.Model.Calls.Single().Request.MaxOutputTokens, "The 1000-token service cap was clamped to the configured limit.");
        Assert.IsNull(rig.Writer.Writes.Single().Snapshot.StaleReason);
    }

    [TestMethod]
    public async Task ACallThatTheSnapshotCannotReproduceIsStoredAsStaleNeverRelabeled()
    {
        var rig = Rig();
        var sink = new TamperingSink(rig.Sink, run => run with { RenderedMessagesHash = "rendered-messages/1:" + new string('0', 64) });

        await Run(Service(rig, sink));

        var write = rig.Writer.Writes.Single();
        Assert.AreEqual(DecisionCaptureRecorder.StaleRenderMismatch, write.Snapshot.StaleReason);
        Assert.AreEqual("rendered-messages/1:" + new string('0', 64), write.Call.RenderedMessagesHash, "The recorded hash is kept as it was.");
    }

    [TestMethod]
    public async Task ASinkThatThrowsChangesNothingTheProposalAndFindingAreIdenticalToACaptureOffRun()
    {
        var withoutRig = Rig();
        var withoutFindings = new FakeFindingStore();
        var without = await Run(Service(withoutRig, null, withoutFindings));
        var rig = Rig();
        var findings = new FakeFindingStore();
        var sink = new ThrowingSink();

        var with = await Run(Service(rig, sink, findings));

        Assert.AreEqual(1, sink.Calls);
        Assert.IsNotNull(with);
        Assert.AreEqual(without!.GiveUp, with.GiveUp);
        CollectionAssert.AreEqual(without.Suggestions.Select(s => s.Value).ToArray(), with.Suggestions.Select(s => s.Value).ToArray());
        Assert.AreEqual(withoutFindings.Findings.Single().Verdict, findings.Findings.Single().Verdict);
        Assert.AreEqual(withoutFindings.Findings.Single().ClaimsJson, findings.Findings.Single().ClaimsJson);
    }

    [TestMethod]
    public async Task AFailingDatabaseWriteIsSwallowedAndLoggedWithoutContent()
    {
        var rig = Rig();
        rig.Writer.Throws = true;

        var proposal = await Run(Service(rig, rig.Sink));

        Assert.IsNotNull(proposal, "The decision was already made; a storage failure cannot take it away.");
        Assert.HasCount(1, rig.Log.Entries);
        Assert.AreEqual("failed:SearchGuidance:InvalidOperationException", rig.Log.Entries[0]);
        Assert.IsFalse(rig.Log.Entries[0].Contains("Lantern", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CaptureOffWritesNothingAndModelCallsAndLedgerMatchACaptureOnRun()
    {
        var off = Rig(capture: false);
        var on = Rig();

        await Run(Service(off, off.Sink));
        await Run(Service(on, on.Sink));

        Assert.IsEmpty(off.Writer.Writes);
        Assert.HasCount(1, on.Writer.Writes);
        Assert.AreEqual(on.Model.Calls.Count, off.Model.Calls.Count);
        Assert.AreEqual(on.Ledger.Records.Count, off.Ledger.Records.Count);
        Assert.AreEqual(on.Ledger.Records.Single().ChargedTokens, off.Ledger.Records.Single().ChargedTokens);
    }

    [TestMethod]
    public async Task AReusedFindingMakesNoCallAndSoSavesNothingMore()
    {
        var rig = Rig();
        var findings = new FakeFindingStore();
        var service = Service(rig, rig.Sink, findings);
        var format = Guid.NewGuid();
        var request = Guid.NewGuid();
        await Run(service, format, request);
        rig.Clock.UtcNow = CaptureRig.Now.AddDays(1);

        await service.GetAsync(request, Guid.NewGuid(), Requested, CaptureData.SearchResults(Requested), ["Lantern Road"], rig.Clock.UtcNow, CancellationToken.None);

        Assert.AreEqual(1, rig.Model.Calls.Count, "The second question reused the finding: no model call.");
        Assert.HasCount(1, rig.Writer.Writes, "No call was made, so there is nothing new to record.");
    }

    [TestMethod]
    public async Task ARefusedCallSavesNothing()
    {
        var rig = new CaptureRig(ModelRole.SearchGuidance, (_, _) => ScriptedModel.Ok(Good), enableRole: false);

        var proposal = await Run(Service(rig, rig.Sink));

        Assert.IsNull(proposal);
        Assert.IsEmpty(rig.Writer.Writes);
        Assert.IsEmpty(rig.Model.Calls);
    }

    [TestMethod]
    public async Task AnInputBeyondTheAllowListIsRefusedWholeNotTrimmedToFit()
    {
        var rig = Rig();
        var hostile = Requested with { Title = "Lantern https://evil.example.test/road" };
        var service = Service(rig, rig.Sink);

        var proposal = await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), hostile, CaptureData.SearchResults(Requested), [], CaptureRig.Now, CancellationToken.None);

        Assert.IsNotNull(proposal, "The decision is unaffected.");
        Assert.IsEmpty(rig.Writer.Writes, "A title the allow-list rejects means no snapshot, not a quietly edited one.");
        Assert.HasCount(1, rig.Log.Entries);
        StringAssert.StartsWith(rig.Log.Entries[0], "failed:SearchGuidance:");
    }

    [TestMethod]
    public async Task AReleaseNameThatEndsInAFileExtensionIsWithheldAndTheSnapshotIsReportedNonExactNotRefused()
    {
        var rig = Rig();
        // Real release names often end in the file extension. A file name must never be stored, so the name is withheld.
        // With no title of its own the release name is what the model was shown for this result.
        var results = new[] { CaptureData.Option(Requested, 1, "The Lantern Road", "Someone Else", "The.Lantern.Road.Someone.Else.epub") with { Title = null } };

        var proposal = await Service(rig, rig.Sink).GetAsync(Guid.NewGuid(), Guid.NewGuid(), Requested, results, [], CaptureRig.Now, CancellationToken.None);

        Assert.IsNotNull(proposal);
        var write = rig.Writer.Writes.Single();
        Assert.IsFalse(write.Snapshot.SnapshotJson.Contains(".epub", StringComparison.OrdinalIgnoreCase), "The file-name-like release name is not stored.");
        Assert.AreEqual(DecisionCaptureRecorder.StaleRenderMismatch, write.Snapshot.StaleReason, "And the snapshot says it cannot reproduce the prompt exactly.");
    }

    [TestMethod]
    public async Task OnlyTheFirstFifteenResultsThatTheModelWasShownAreKept()
    {
        var rig = Rig();
        var many = Enumerable.Range(1, 20).Select(n => CaptureData.Option(Requested, n, $"Title {n}", "Mara Quill", $"Title.{n}")).ToArray();

        await Service(rig, rig.Sink).GetAsync(Guid.NewGuid(), Guid.NewGuid(), Requested, many, [], CaptureRig.Now, CancellationToken.None);

        var write = rig.Writer.Writes.Single();
        Assert.HasCount(15, write.Links);
        Assert.IsNull(write.Snapshot.StaleReason);
    }

    private sealed class TamperingSink(IDecisionCaptureSink inner, Func<DecisionCaptureRun, DecisionCaptureRun> change) : IDecisionCaptureSink
    {
        public Task CaptureAsync(CapturedDecision decision, CancellationToken cancellationToken) =>
            inner.CaptureAsync(decision with { Run = change(decision.Run) }, cancellationToken);
    }
}
