using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTuning;

[TestClass]
public sealed class TuningCoreTests
{
    private static readonly BuiltInTuningCorpus Corpus = BuiltInTuningCorpus.LoadEmbedded();
    private static readonly TuningCalibrationKey Key = new("synthetic-1", "draft-1", 1, "test-only-frozen-identity");
    private static readonly TuningModelKey Model = new("connection-local", "example-model");
    private static readonly HashSet<string> RetainPickControlIds = ["CT-63", "CT-65", "CT-68", "CT-74", "CT-75", "CT-78", "CT-81"];
    private static TuningCase Case(string id) => Corpus.Cases.Single(c => c.Case.Id == id).Case;
    private static TuningCompletion Completion(string? reply) => new(ModelCallOutcome.Succeeded, reply);
    private static TuningResultIdentity Identity(TuningCase item) => new(TuningCaseIdentity.From(item), "synthetic-1", "prompt", "parser", Model, null, ModelReasoningMode.Inherit, 0);

    [TestMethod]
    public void EveryReferenceAnswerUsesTheRealRendererParserAndPolicyAndHasAnExactDriftFingerprint()
    {
        Assert.HasCount(280, Corpus.Cases);
        foreach (var fixture in Corpus.Cases)
        {
            var rendered = TuningCaseRenderer.Render(fixture.Case);
            Assert.AreEqual(fixture.PromptIdentity, rendered.PromptIdentity, fixture.Case.Id);
            Assert.IsGreaterThanOrEqualTo(fixture.Case.Expected.Acceptable.Count, fixture.ReferenceReplies.Count, fixture.Case.Id);
            foreach (var alternative in fixture.Case.Expected.Acceptable)
                Assert.IsTrue(fixture.ReferenceReplies.Any(reply => TuningScoring.Score(fixture.Case with
                { Expected = fixture.Case.Expected with { Acceptable = [alternative] } }, rendered, Completion(reply)).Score == TuningScore.Correct), fixture.Case.Id);
            foreach (var reply in fixture.ReferenceReplies)
            {
                var score = TuningScoring.Score(fixture.Case, rendered, Completion(reply));
                Assert.IsTrue(score.IsValid, fixture.Case.Id);
                Assert.AreEqual(TuningScore.Correct, score.Score, fixture.Case.Id);
                Assert.IsFalse(score.SafetyViolation, fixture.Case.Id);
            }
            var roundTrip = TuningInputJson.Read(fixture.Case.Role, TuningInputJson.Write(fixture.Case.Role, fixture.Case.Input));
            Assert.AreEqual(rendered.PromptIdentity, TuningCaseRenderer.Render(fixture.Case with { Input = roundTrip }).PromptIdentity);
            Assert.AreEqual(rendered.PromptIdentity, TuningCaseRenderer.Render(fixture.Case.Role, roundTrip,
                fixture.Case.Source, fixture.Case.Coverage, fixture.Case.SyntheticSupplement).PromptIdentity);
        }
    }

    [TestMethod]
    public void CorpusKeepsReviewStateBalanceAndIndependentRoles()
    {
        Assert.HasCount(280, Corpus.Cases.Where(c => c.Case.Review.State == LabelReviewState.Approved).ToArray());
        Assert.HasCount(7, Corpus.Cases.Where(c => RetainPickControlIds.Contains(c.Case.Id)).ToArray());
        string[] retiredIds = ["CT-61", "CT-62", "CT-64", "CT-66", "CT-67", "CT-69", "CT-70", "CT-71", "CT-72", "CT-73", "CT-76", "CT-77", "CT-79", "CT-80"];
        Assert.IsFalse(Corpus.Cases.Any(c => retiredIds.Contains(c.Case.Id, StringComparer.Ordinal)));
        foreach (var role in TuningRoles.Scored)
        {
            var cases = Corpus.Cases.Where(c => c.Case.Role == role).Select(c => c.Case).ToArray();
            Assert.AreEqual(role is ModelRole.CandidateTriage or ModelRole.ArtifactValidationDecision ? 60 : 40, cases.Length);
            Assert.AreEqual(cases.Length, cases.Select(c => TuningCaseRenderer.Render(c).PromptIdentity).Distinct(StringComparer.Ordinal).Count(),
                "Repeated prompts cannot pad the role's sample size.");
            Assert.IsTrue(cases.Count(TuningScoring.RequiresNonAbstention) >= cases.Length * .4);
            Assert.IsTrue(TuningScoring.AbstainBaseline(cases) <= .6m);
            Assert.IsFalse(TuningRoles.IsScored(ModelRole.ConversationResponse));
            Assert.IsFalse(TuningRoles.IsScored(ModelRole.VisionCoverIdentification));
        }
        Assert.AreEqual(.275m, TuningScoring.AbstainBaseline(Corpus.Cases.Where(c => c.Case.Role == ModelRole.ArtifactValidationAssessment).Select(c => c.Case).ToArray()));
        Assert.AreEqual(.5m, TuningScoring.AbstainBaseline(Corpus.Cases.Where(c => c.Case.Role == ModelRole.ArtifactValidationDecision).Select(c => c.Case).ToArray()));
        Assert.AreEqual(.5m, TuningScoring.AbstainBaseline(Corpus.Cases.Where(c => c.Case.Role == ModelRole.CandidateTriage).Select(c => c.Case).ToArray()));
        Assert.AreEqual(.15m, TuningScoring.AbstainBaseline(Corpus.Cases.Where(c => c.Case.Role == ModelRole.IntentInterpretation).Select(c => c.Case).ToArray()));
    }

    [TestMethod]
    [DataRow("SG-08")]
    [DataRow("SG-09")]
    [DataRow("SG-10")]
    [DataRow("SG-11")]
    public void OriginalTitleAlsoAcceptsTheEquivalentAlternateTitleReasonWithoutAcceptingAnInventedName(string id)
    {
        var item = Case(id);
        var rendered = TuningCaseRenderer.Render(item);
        foreach (var reason in new[] { "original_language_title", "alternate_published_title" })
        {
            var reply = TuningReplies.Render(item.Role,
                new(GiveUp: false, AllowedSuggestions: [new(reason, "The Amber Atrium")]), rendered);
            var score = TuningScoring.Score(item, rendered, Completion(reply));
            Assert.IsTrue(score.IsValid);
            Assert.AreEqual(TuningScore.Correct, score.Score);
        }
        var unsupported = TuningReplies.Render(item.Role,
            new(GiveUp: false, AllowedSuggestions: [new("alternate_published_title", "An Unrelated Control Title")]), rendered);
        var wrong = TuningScoring.Score(item, rendered, Completion(unsupported));
        Assert.IsTrue(wrong.IsValid);
        Assert.AreEqual(TuningScore.Incorrect, wrong.Score);
    }

    [TestMethod]
    public void AProposedLabelCannotCertifyAdequacyEvenWhenOtherLabelsAreApprovedAndRepliesArePerfect()
    {
        const ModelRole role = ModelRole.CandidateTriage;
        var fixtures = Corpus.Cases.Where(c => c.Case.Role == role).ToArray();
        var cases = fixtures.Select(f => f.Case.Id == "CT-63"
            ? f.Case with { Review = new(LabelReviewState.Proposed, 1) } : f.Case).ToArray();
        var observations = fixtures.SelectMany(f => Enumerable.Range(0, 2).Select(repeat =>
            new CaseObservation(TuningCaseIdentity.From(f.Case), repeat,
                TuningScoring.Score(f.Case, TuningCaseRenderer.Render(f.Case), Completion(f.ReferenceReplies[0]))))).ToArray();
        var score = TuningMetrics.Summarize(cases, observations, 2, Corpus.CalibrationKey(role));
        Assert.AreEqual(1m, score.TaskScore);
        Assert.IsFalse(score.LabelsApproved);
        Assert.AreEqual(TuningAdequacy.Incomplete, TuningAdequacyRules.Assess(score, null));
    }

    [TestMethod]
    public async Task EveryTriageCaseReachesTheProductionExecutionPolicyWithIdenticalMessages()
    {
        foreach (var fixture in Corpus.Cases.Where(c => c.Case.Role == ModelRole.CandidateTriage))
        {
            var input = (CandidateTriageCaseInput)fixture.Case.Input;
            var context = TuningCaseRenderer.BuildTriageContext(input);
            var rendered = TuningCaseRenderer.Render(fixture.Case);
            foreach (var reply in fixture.ReferenceReplies)
            {
                var kit = new TriageExecution(reply);
                var store = new FakeFindingStore();
                var result = await new CandidateTriageService(kit, kit, store, kit).GetAsync(Guid.NewGuid(), Guid.NewGuid(),
                    "example-source", context, CancellationToken.None);
                Assert.AreEqual(1, kit.Resolutions, fixture.Case.Id);
                Assert.AreEqual(1, kit.Calls, fixture.Case.Id);
                Assert.AreEqual(ModelCallOutcome.Succeeded, store.Findings.Single().Outcome, fixture.Case.Id);
                Assert.IsNotNull(kit.Request);
                Assert.AreEqual(ModelRole.CandidateTriage, kit.Request.Role);
                Assert.AreEqual(fixture.PromptIdentity, RenderedMessagesIdentity.Compute(kit.Request.Messages,
                    kit.Request.JsonObjectOutput, kit.Request.MaxOutputTokens), fixture.Case.Id);
                var expected = rendered.Parse(reply).Answer!;
                if (expected.Move is "keep" or "uncertain") Assert.IsNull(result, fixture.Case.Id);
                else
                {
                    Assert.IsNotNull(result, fixture.Case.Id);
                    Assert.AreEqual(expected.Move, result.Move, fixture.Case.Id);
                    Assert.AreEqual(expected.TargetKey, result.Candidate.ProviderResultId, fixture.Case.Id);
                }
            }
            if (RetainPickControlIds.Contains(fixture.Case.Id))
            {
                Assert.IsNotNull(context.Pick);
                Assert.AreEqual(CandidateConfidenceTier.Strong, CandidateConfidence.Classify(context.Pick), fixture.Case.Id);
                var eligible = context.Candidates.Where(c => CandidateConfidence.Allows(context.Level, CandidateConfidence.Classify(c)));
                Assert.AreEqual(context.Pick, ExternalCandidateRanker.SelectBest(eligible, context.Pick.MediaType), fixture.Case.Id);
                foreach (var move in new[] { "veto", "author_variant", "prefer" })
                    Assert.IsFalse(rendered.Parse(TuningReplies.Render(ModelRole.CandidateTriage,
                        new(Move: move, TargetKey: "first"), rendered)).IsValid, fixture.Case.Id + " " + move);
            }
        }
    }

    [TestMethod]
    public async Task RemovingTheCurrentPickReproducesQuarantineAndCannotCountAsProductionReachable()
    {
        foreach (var fixture in Corpus.Cases.Where(c => RetainPickControlIds.Contains(c.Case.Id)))
        {
            var input = (CandidateTriageCaseInput)fixture.Case.Input;
            var unreachable = input with { Candidates = input.Candidates.Where(c => c.Candidate.Key != "selected").ToArray(), PickKey = null };
            var item = fixture.Case with { Input = unreachable };
            Assert.ThrowsExactly<ArgumentException>(() => TuningCaseRenderer.Render(item), item.Id);
            Assert.IsNotNull(TuningCaseRenderer.Render(item with { Coverage = TuningCoverage.ContractOnly }));
            var kit = new TriageExecution(fixture.ReferenceReplies[0]);
            var store = new FakeFindingStore();
            await new CandidateTriageService(kit, kit, store, kit).GetAsync(Guid.NewGuid(), Guid.NewGuid(),
                "example-source", TuningCaseRenderer.BuildTriageContext(unreachable), CancellationToken.None);
            Assert.AreEqual(0, kit.Resolutions, item.Id);
            Assert.AreEqual(0, kit.Calls, item.Id);
            Assert.HasCount(0, store.Findings);
        }
    }

    [TestMethod]
    public void ReviewedTriageContractsPreserveBaselinesWithoutMeasuringOptionalActionsAsAbility()
    {
        var fixtures = Corpus.Cases.Where(c => c.Case.Role == ModelRole.CandidateTriage).ToArray();
        var cases = fixtures.Select(f => f.Case).ToArray();
        int CorrectConstant(string move) => fixtures.Count(f =>
        {
            var rendered = TuningCaseRenderer.Render(f.Case);
            return TuningScoring.Score(f.Case, rendered,
                Completion(TuningReplies.Render(ModelRole.CandidateTriage, new(Move: move), rendered))).Score == TuningScore.Correct;
        });
        Assert.AreEqual(30, CorrectConstant("keep"));
        Assert.AreEqual(19, CorrectConstant("uncertain"));
        var baseline = TuningScoring.EvaluateAbstainBaseline(cases);
        Assert.AreEqual(.5m, baseline.Score);
        Assert.AreEqual(30, baseline.Correct);
        Assert.AreEqual(30, baseline.Incorrect);
        Assert.AreEqual(26, cases.Count(TuningScoring.RequiresNonAbstention));

        var contracts = fixtures.Where(f => f.Case.Expected.Acceptable.Any(a => a.Move is "veto" or "prefer")).ToArray();
        Assert.HasCount(10, contracts);
        Assert.HasCount(5, contracts.Where(f => f.Case.Expected.Acceptable.Any(a => a.Move == "veto")).ToArray());
        Assert.HasCount(5, contracts.Where(f => f.Case.Expected.Acceptable.Any(a => a.Move == "prefer")).ToArray());
        foreach (var fixture in contracts)
        {
            Assert.IsTrue(fixture.Case.Expected.Acceptable.Any(a => a.Move == "keep" && a.TargetKey is null));
            Assert.IsFalse(TuningScoring.RequiresNonAbstention(fixture.Case));
        }
        var observations = contracts.Select(f => new CaseObservation(TuningCaseIdentity.From(f.Case), 0,
            TuningScoring.Score(f.Case, TuningCaseRenderer.Render(f.Case), Completion(f.ReferenceReplies[0])))).ToArray();
        var score = TuningMetrics.Summarize(contracts.Select(f => f.Case).ToArray(), observations, 1,
            Corpus.CalibrationKey(ModelRole.CandidateTriage));
        Assert.AreEqual(1m, score.TaskScore);
        Assert.IsNull(score.ActionRecall, "Optional successful veto/prefer contracts are not required-action ability.");
        Assert.IsNull(score.Stability, "One repeat cannot measure repeat agreement.");
    }

    [TestMethod]
    [DataRow("CT-10")]
    [DataRow("CT-17")]
    [DataRow("CT-24")]
    [DataRow("CT-31")]
    public void ConflictingTitleAndCreditPermitVariantOrUncertaintyButNotKeep(string id)
    {
        var item = Case(id);
        var input = (CandidateTriageCaseInput)item.Input;
        var candidate = input.Candidates.Single().Candidate;
        StringAssert.Contains(candidate.Work.Title, input.Requested.Author!);
        Assert.AreEqual("Orin Dallow", candidate.Work.Authors.Single().Name);
        var rendered = TuningCaseRenderer.Render(item);
        Assert.HasCount(2, item.Expected.Acceptable);
        Assert.AreEqual(TuningScore.Correct, TuningScoring.Score(item, rendered,
            Completion(TuningReplies.Render(item.Role, new(Move: "author_variant", TargetKey: candidate.Key), rendered))).Score);
        Assert.AreEqual(TuningScore.Correct, TuningScoring.Score(item, rendered,
            Completion(TuningReplies.Render(item.Role, new(Move: "uncertain"), rendered))).Score);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered,
            Completion(TuningReplies.Render(item.Role, new(Move: "keep"), rendered))).Score);
    }

    [TestMethod]
    public void ArtifactFixturesUseCompatibleMediaAndUniqueRecordedEvidence()
    {
        foreach (var item in Corpus.Cases.Select(c => c.Case))
        {
            if (item.Input is ArtifactAssessmentInput assessment)
            {
                Assert.AreEqual(assessment.Recorded.Count,
                    assessment.Recorded.Select(e => (e.RecordKind, e.FindingId)).Distinct().Count(), item.Id);
                if (assessment.Recorded.Any(e => e.Dimension == EvidenceDimension.AudioTechnical ||
                    e.Code.StartsWith("artifact.audio.", StringComparison.Ordinal) || e.Code == EvidenceReasonCodes.ArtifactIdentityCorroboration.TitleTrackOnly))
                    Assert.AreEqual(RequestMediaType.Audiobook, assessment.Asset.MediaType, item.Id);
            }
            if (item.Input is ArtifactHoldInput { Asset.MediaType: RequestMediaType.Audiobook } hold)
                Assert.IsNull(hold.ExpectedProfile.CatalogPageCount, item.Id);
        }
    }

    [TestMethod]
    [DataRow("AVA-24")]
    [DataRow("AVA-29")]
    [DataRow("AVA-30")]
    public void AuthorOnlySupportRemainsConsistentWithoutAnyRenderedNegative(string id)
    {
        var item = Case(id);
        var rendered = TuningCaseRenderer.Render(item);
        Assert.IsFalse(rendered.Findings.Any(f => f.Polarity == EvidencePolarity.Negative));
        Assert.AreEqual(EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorAgrees,
            rendered.Findings.Single(f => f.Polarity == EvidencePolarity.Positive).Code);
        Assert.AreEqual("consistent", item.Expected.Acceptable.Single().Verdict);
        Assert.AreEqual(TuningScore.Correct, TuningScoring.Score(item, rendered,
            Completion(TuningReplies.Render(item.Role, new(Verdict: "consistent"), rendered))).Score);
    }

    [TestMethod]
    public void ParserPolicyAndScorerSourceChangesInvalidateTheContractRevision()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FamilyLibrarian.slnx"))) root = root.Parent;
        Assert.IsNotNull(root, "Repository root is required for the contract revision guard.");
        foreach (var (path, expected) in TuningContractIdentity.Snapshot.Sources)
        {
            var source = File.ReadAllText(Path.Combine(root.FullName, path)).ReplaceLineEndings("\n");
            Assert.AreEqual(expected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))), path);
        }
        var trackedCore = TuningContractIdentity.Snapshot.Sources.Keys.Where(p => p.Contains("/ModelTuning/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var actualCore = Directory.GetFiles(Path.Combine(root.FullName, "src/FamilyLibrarian.Application/ModelTuning"), "*.cs")
            .Select(p => Path.GetRelativePath(root.FullName, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(actualCore, trackedCore);
        Assert.AreNotEqual(Corpus.CalibrationKey(ModelRole.IntentInterpretation), Corpus.CalibrationKey(ModelRole.CandidateTriage));
    }

    [TestMethod]
    public void RenderedMessagesHashHasIndependentGoldenVectorsAndPreservesBoundaries()
    {
        Assert.AreEqual("rendered-messages/1:ba2f0e3483f23b9f71769c7f7e68017d60649d17eec4c1630523996fed39b1ff", RenderedMessagesIdentity.Compute([], false, null));
        ModelChatMessage[] messages = [new("system", "Héllo\r\n"), new("user", "a|b")];
        Assert.AreEqual("rendered-messages/1:c26b65278433a7f422facef691eb28a2918e394e80592e548afa19972a502d45", RenderedMessagesIdentity.Compute(messages, true, 120));
        var hash = RenderedMessagesIdentity.Compute(messages, true, 120);
        Assert.AreNotEqual(hash, RenderedMessagesIdentity.Compute(messages.Reverse().ToArray(), true, 120));
        Assert.AreNotEqual(hash, RenderedMessagesIdentity.Compute(messages, false, 120));
        Assert.AreNotEqual(hash, RenderedMessagesIdentity.Compute(messages, true, null));
        Assert.AreNotEqual(hash, RenderedMessagesIdentity.Compute(messages, true, 121));
        Assert.AreNotEqual(hash, RenderedMessagesIdentity.Compute([new("system", "He\u0301llo\r\n"), messages[1]], true, 120));
        Assert.AreNotEqual(hash, RenderedMessagesIdentity.Compute([new("system", "Héllo\n"), messages[1]], true, 120));
        Assert.AreNotEqual(RenderedMessagesIdentity.Compute([new("user", "a"), new("user", "b")], false, null),
            RenderedMessagesIdentity.Compute([new("user", "auserb")], false, null));
        Assert.ThrowsExactly<EncoderFallbackException>(() => RenderedMessagesIdentity.Compute([new("user", "\ud800")], true, 120));
        Assert.ThrowsExactly<ArgumentNullException>(() => RenderedMessagesIdentity.Compute([new("user", null!)], true, 120));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RenderedMessagesIdentity.Compute(messages, true, 0));
        Assert.ThrowsExactly<ArgumentException>(() => RenderedMessagesIdentity.Compute([new("user", "text", [new("image/png", [])])], true, 120));
    }

    [TestMethod]
    public async Task IntentFormatterMatchesTheActualProductionRequestAndKeepsEmptyMessageBehavior()
    {
        var policy = new CapturingPolicy();
        var interpreter = new ModelBookIntentInterpreter(policy);
        var item = Case("II-02");
        var input = (IntentCaseInput)item.Input;
        await interpreter.InterpretAsync(Guid.NewGuid(), input.Message, input.PreviousSearch, CancellationToken.None);
        var actual = policy.Request!;
        Assert.AreEqual(TuningCaseRenderer.Render(item).PromptIdentity,
            RenderedMessagesIdentity.Compute(actual.Messages, actual.JsonObjectOutput, actual.MaxOutputTokens));
        policy.Request = null;
        Assert.IsNull(await interpreter.InterpretAsync(Guid.NewGuid(), "   ", "previous", CancellationToken.None));
        Assert.IsNull(policy.Request);
        Assert.AreEqual("MESSAGE: Find a book\nPREVIOUS SEARCH: Last search", ModelBookIntentInterpreter.RenderUserContent(" Find   a book ", "Last  search"));
    }

    [TestMethod]
    [DataRow("providerData")]
    [DataRow("acquireToken")]
    [DataRow("inspectionUri")]
    [DataRow("sha256")]
    [DataRow("storedFilename")]
    [DataRow("extensionsJson")]
    [DataRow("excerpts")]
    public void SnapshotSchemaRejectsOperationalFieldsAtNestedLevels(string field)
    {
        var item = Case("CAA-01");
        var json = JsonNode.Parse(TuningInputJson.Write(item.Role, item.Input))!;
        json["candidate"]![field] = "excluded";
        Assert.ThrowsExactly<JsonException>(() => TuningInputJson.Read(item.Role, json.ToJsonString()));
    }

    [TestMethod]
    public void SnapshotSchemaRejectsDuplicateMissingNullUnsupportedAndExcludedValues()
    {
        var item = Case("II-01");
        Assert.ThrowsExactly<JsonException>(() => TuningInputJson.Read(item.Role, "{\"schemaVersion\":1,\"schemaVersion\":1,\"message\":\"a\",\"previousSearch\":null}"));
        Assert.ThrowsExactly<JsonException>(() => TuningInputJson.Read(item.Role, "{\"schemaVersion\":1,\"previousSearch\":null}"));
        Assert.ThrowsExactly<JsonException>(() => TuningInputJson.Read(item.Role, "{\"schemaVersion\":1,\"message\":null,\"previousSearch\":null}"));
        Assert.ThrowsExactly<JsonException>(() => TuningInputJson.Read(item.Role, "{\"schemaVersion\":2,\"message\":\"a\",\"previousSearch\":null}"));
        var candidate = Case("CAA-01");
        foreach (var name in new[] { "/private/file", "copy.epub", new string('a', 64), "https://example.test/private" })
        {
            var json = JsonNode.Parse(TuningInputJson.Write(candidate.Role, candidate.Input))!;
            json["candidate"]!["releaseFacts"]!["name"] = name;
            Assert.ThrowsExactly<JsonException>(() => TuningInputJson.Read(candidate.Role, json.ToJsonString()));
        }
    }

    [TestMethod]
    public void FailuresWrongAnswersAndUnknownHaveDistinctAccounting()
    {
        var item = Case("CT-01"); var rendered = TuningCaseRenderer.Render(item);
        var household = item with { Source = TuningSource.Household };
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(household, rendered, new(ModelCallOutcome.Timeout, "ignored"), false).Score);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(household, rendered, Completion("{}"), false).Score);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(household, rendered with { Parse = _ => new(false, new(Move: "keep"), []) }, Completion("retained invalid answer"), false).Score);
        var keep = TuningReplies.Render(item.Role, new(Move: "keep"), rendered);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered, Completion(keep)).Score);
        Assert.AreEqual(TuningScore.Unknown, TuningScoring.Score(household, rendered, Completion(keep), false).Score);
        Assert.ThrowsExactly<ArgumentException>(() => TuningScoring.Score(item, rendered, Completion(keep), false));
        var unknownBaseline = TuningScoring.EvaluateAbstainBaseline([Case("II-01") with { Source = TuningSource.Household }], (_, _) => false);
        Assert.IsNull(unknownBaseline.Score); Assert.AreEqual(1, unknownBaseline.Unknown); Assert.AreEqual(0, unknownBaseline.Incorrect);
        var correct = Corpus.Cases.Single(c => c.Case.Id == item.Id).ReferenceReplies[0];
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(household, rendered, new(ModelCallOutcome.Timeout, correct), false).Score);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered, new(ModelCallOutcome.Succeeded, correct, true)).Score);
        var wrongTarget = JsonNode.Parse(correct)!; wrongTarget["candidate"] = "c99";
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered, Completion(wrongTarget.ToJsonString())).Score);
        var citations = JsonNode.Parse(correct)!; citations["evidence_ids"] = new JsonArray("invented");
        Assert.IsFalse(TuningScoring.Score(item, rendered, Completion(citations.ToJsonString())).IsValid);
    }

    [TestMethod]
    public void SearchScoringDoesNotRewardAnInventedNewNameOrWrongEvidence()
    {
        var fixture = Corpus.Cases.Single(c => c.Case.Id == "SG-01");
        var rendered = TuningCaseRenderer.Render(fixture.Case);
        var reply = JsonNode.Parse(fixture.ReferenceReplies[0])!;
        reply["suggestions"]![0]!["value"] = "The Invented Wrong Search";
        var scored = TuningScoring.Score(fixture.Case, rendered, Completion(reply.ToJsonString()));
        Assert.IsTrue(scored.IsValid); Assert.AreEqual(TuningScore.Incorrect, scored.Score);
        var extra = JsonNode.Parse(fixture.ReferenceReplies[0])!;
        extra["suggestions"]!.AsArray().Add(reply["suggestions"]![0]!.DeepClone());
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(fixture.Case, rendered, Completion(extra.ToJsonString())).Score);
        reply["suggestions"]![0]!["evidence_ids"] = new JsonArray("forged");
        Assert.IsFalse(TuningScoring.Score(fixture.Case, rendered, Completion(reply.ToJsonString())).IsValid);
    }

    [TestMethod]
    public void ReviewedRequiredCitationsAndRequiredAliasAreNotOptional()
    {
        var item = Case("AVA-01"); var input = (ArtifactAssessmentInput)item.Input;
        item = item with { Input = input with { Recorded = input.Recorded.Concat([new TuningEvidence("identity-verification", "ev-author-conflict",
            FamilyLibrarian.Domain.Evidence.EvidenceDimension.IdentityCorroboration, "artifact.identity_author.differs",
            FamilyLibrarian.Domain.Evidence.EvidencePolarity.Negative, "Orin Dallow")]).ToArray() } };
        var rendered = TuningCaseRenderer.Render(item);
        var reply = TuningReplies.Render(item.Role, new(Verdict: "inconsistent"), rendered);
        Assert.IsTrue(rendered.Parse(reply).IsValid);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered, Completion(reply)).Score);
        var search = Case("SG-01"); var label = search.Expected.Acceptable[0];
        var other = new TuningSearchName("alternate_published_title", "The Willow Register");
        search = search with { Expected = search.Expected with { Acceptable = [label with { AllowedSuggestions = label.AllowedSuggestions!.Concat([other]).ToArray() }] } };
        rendered = TuningCaseRenderer.Render(search);
        reply = TuningReplies.Render(search.Role, new(GiveUp: false, RequiredSuggestions: [other]), rendered);
        Assert.IsTrue(rendered.Parse(reply).IsValid);
        Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(search, rendered, Completion(reply)).Score);
    }

    [TestMethod]
    public void AllowedMoveOnTheWrongSuppliedCandidateIsIncorrect()
    {
        var item = Case("CT-01"); var input = (CandidateTriageCaseInput)item.Input;
        var second = input.Candidates[0] with { Candidate = input.Candidates[0].Candidate with { Key = "second" } };
        item = item with { Input = input with { Candidates = [input.Candidates[0], second] } };
        var rendered = TuningCaseRenderer.Render(item);
        var reply = TuningReplies.Render(item.Role, new(Move: "author_variant", TargetKey: "second"), rendered);
        var result = TuningScoring.Score(item, rendered, Completion(reply));
        Assert.IsTrue(result.IsValid); Assert.AreEqual(TuningScore.Incorrect, result.Score);
        var strict = item with { Input = ((CandidateTriageCaseInput)item.Input) with { Confidence = FamilyLibrarian.Domain.Providers.ProviderConfidenceLevel.Strict } };
        var strictRendered = TuningCaseRenderer.Render(strict);
        Assert.IsFalse(TuningScoring.Score(strict, strictRendered, Completion(TuningReplies.Render(item.Role,
            new(Move: "author_variant", TargetKey: "first"), strictRendered))).IsValid);
    }

    [TestMethod]
    public void NegativeAmbiguityIsASeparateContractCaseAndMissingEvidenceCannotContradict()
    {
        var item = Case("CAA-01"); var input = (CandidateAssessmentInput)item.Input;
        item = item with { Coverage = TuningCoverage.ContractOnly, Input = input with { Candidate = input.Candidate with
            { Work = input.Candidate.Work with { Title = "The Copper Grove" } } },
            Expected = new([new(Verdict: "inconsistent")], [], []) };
        var rendered = TuningCaseRenderer.Render(item);
        Assert.AreEqual(TuningScore.Correct, TuningScoring.Score(item, rendered, Completion(TuningReplies.Render(item.Role, new(Verdict: "inconsistent"), rendered))).Score);
        Assert.ThrowsExactly<ArgumentException>(() => TuningCaseRenderer.Render(item with { Coverage = TuningCoverage.ProductionReachable }));
        var unknown = Case("CAA-03"); var unknownRendered = TuningCaseRenderer.Render(unknown);
        var invalid = "{\"schema\":\"candidate-assessment/1\",\"verdict\":\"inconsistent\",\"claims\":[{\"kind\":\"contradicts_same_work\",\"evidence_ids\":[\"ev-decision\"],\"note\":\"Not enough evidence.\"}]}";
        Assert.IsFalse(TuningScoring.Score(unknown, unknownRendered, Completion(invalid)).IsValid);
    }

    [TestMethod]
    public void ExcerptsAreSyntheticOnlyAndTheRealParserRejectsCopiedNotes()
    {
        var item = Case("AVD-01");
        const string excerpt = "This is a free sample that ends before the complete story begins.";
        item = item with { SyntheticSupplement = new(1, [new("tc-last", excerpt)]) };
        var rendered = TuningCaseRenderer.Render(item);
        var reply = JsonNode.Parse(TuningReplies.Render(item.Role, new(Verdict: "hold"), rendered))!;
        reply["claims"]![0]!["note"] = excerpt;
        Assert.IsFalse(TuningScoring.Score(item, rendered, Completion(reply.ToJsonString())).IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => TuningCaseRenderer.Render(item with { Source = TuningSource.Household }));
        Assert.IsFalse(TuningInputJson.Write(item.Role, item.Input).Contains(excerpt, StringComparison.Ordinal));
        var benign = Case("AVD-03"); var input = (ArtifactHoldInput)benign.Input;
        var withheld = TuningCaseRenderer.Render(benign with { Input = input with { IncludeDetails = false }, SyntheticSupplement = item.SyntheticSupplement });
        Assert.IsFalse(withheld.Messages.Any(m => m.Content.Contains(excerpt, StringComparison.Ordinal)));
        Assert.IsFalse(withheld.Findings.Any(f => f.Id == "tc-sample-marker"));
    }

    [TestMethod]
    public void RepeatedSearchesAreFilteredByProductionButFailuresAreNotHidden()
    {
        var item = Case("SG-01"); var rendered = TuningCaseRenderer.Render(item);
        var reply = JsonNode.Parse(Corpus.Cases.Single(c => c.Case.Id == item.Id).ReferenceReplies[0])!;
        var repeated = reply["suggestions"]![0]!.DeepClone(); repeated["value"] = "The Brass Orchard";
        reply["suggestions"]!.AsArray().Add(repeated);
        Assert.AreEqual(TuningScore.Correct, TuningScoring.Score(item, rendered, Completion(reply.ToJsonString())).Score);
        reply["suggestions"]!.AsArray().RemoveAt(0);
        Assert.IsFalse(TuningScoring.Score(item, rendered, Completion(reply.ToJsonString())).IsValid);
    }

    [TestMethod]
    public void EmbeddedLoaderRejectsSourceCountAndLabelCorruption()
    {
        static string Read(string name)
        {
            using var stream = typeof(BuiltInTuningCorpus).Assembly.GetManifestResourceStream("FamilyLibrarian.ModelTuning." + name)!;
            using var reader = new StreamReader(stream); return reader.ReadToEnd();
        }
        var manifest = Read("manifest.json");
        Assert.ThrowsExactly<JsonException>(() => BuiltInTuningCorpus.Load(manifest.Replace("\"caseCount\": 40", "\"caseCount\": 39", StringComparison.Ordinal), Read));
        Assert.ThrowsExactly<JsonException>(() => BuiltInTuningCorpus.Load(manifest, name => Read(name).Replace("\"source\": \"builtIn\"", "\"source\": \"household\"", StringComparison.Ordinal)));
        Assert.ThrowsExactly<JsonException>(() => BuiltInTuningCorpus.Load(manifest, name => name == "IntentInterpretation.json"
            ? Read(name).Replace("\"II-02\"", "\"II-01\"", StringComparison.Ordinal) : Read(name)));
        Assert.ThrowsExactly<JsonException>(() => BuiltInTuningCorpus.Load(manifest, name => name == "ArtifactValidationAssessment.json"
            ? Read(name).Replace("\"verdict\": \"inconsistent\"", "\"verdict\": \"uncertain\"", StringComparison.Ordinal)
                .Replace("\"verdict\": \"consistent\"", "\"verdict\": \"uncertain\"", StringComparison.Ordinal) : Read(name)));
        Assert.ThrowsExactly<JsonException>(() => BuiltInTuningCorpus.Load(manifest, name => name == "CandidateAmbiguityAssessment.json"
            ? Read(name).Replace("\"verdict\": \"uncertain\"", "\"verdict\": \"consistent\"", StringComparison.Ordinal) : Read(name)));
    }

    [TestMethod]
    public void PreferCurrentPickAcceptedByTheParserIsStillASafetyDisqualifier()
    {
        var item = Case("CT-03"); var rendered = TuningCaseRenderer.Render(item);
        var reply = TuningReplies.Render(item.Role, new(Move: "prefer", TargetKey: "first"), rendered);
        var score = TuningScoring.Score(item, rendered, Completion(reply));
        Assert.IsTrue(score.SafetyViolation); Assert.AreEqual(TuningScore.Incorrect, score.Score);
    }

    [TestMethod]
    public void HouseholdResultHasNoRawReplyAndCannotBeRetaggedOrSmuggleNestedText()
    {
        var item = Case("II-01") with { Source = TuningSource.Household };
        var rendered = TuningCaseRenderer.Render(item);
        var reply = Corpus.Cases.Single(c => c.Case.Id == item.Id).ReferenceReplies[0];
        var score = TuningScoring.Score(item, rendered, Completion(reply));
        var result = TuningResultJson.Create(item, Identity(item), score, reply + " RAW_SECRET");
        Assert.IsInstanceOfType<HouseholdCaseResult>(result);
        var json = TuningResultJson.Write(item, result);
        Assert.IsFalse(json.Contains("RAW_SECRET", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("rawReply", StringComparison.OrdinalIgnoreCase));
        Assert.IsInstanceOfType<HouseholdCaseResult>(TuningResultJson.Read(item, json));
        Assert.ThrowsExactly<ArgumentException>(() => TuningResultJson.Write(item,
            new BuiltInCaseResult(Identity(item), score.Answer, score.Score, null)));
        foreach (var field in new[] { "rawReply", "rawReplyPreview", "content" })
        {
            var node = JsonNode.Parse(json)!; node[field] = "raw";
            Assert.ThrowsExactly<JsonException>(() => TuningResultJson.Read(item, node.ToJsonString()));
            node = JsonNode.Parse(json)!; node["normalizedAnswer"]![field] = "raw";
            Assert.ThrowsExactly<JsonException>(() => TuningResultJson.Read(item, node.ToJsonString()));
        }
        var forged = JsonNode.Parse(json)!; forged["source"] = "builtIn";
        Assert.ThrowsExactly<JsonException>(() => TuningResultJson.Read(item, forged.ToJsonString()));
        var injected = score with { Answer = new(Intent: "find_book", Title: "{raw reply}") };
        Assert.ThrowsExactly<ArgumentException>(() => TuningResultJson.Create(item, Identity(item), injected, null));
    }

    [TestMethod]
    public void BuiltInPreviewIsOnlyForFailuresAndTruncatesOnUnicodeBoundaries()
    {
        var item = Case("II-01");
        var failure = new CaseScore(TuningScore.Incorrect, false, false, null);
        var result = (BuiltInCaseResult)TuningResultJson.Create(item, Identity(item), failure, string.Concat(Enumerable.Repeat("🌻", 500)));
        Assert.AreEqual(1024, Encoding.UTF8.GetByteCount(result.RawReplyPreview!));
        var good = new CaseScore(TuningScore.Correct, true, false, item.Expected.Acceptable[0]);
        Assert.IsNull(((BuiltInCaseResult)TuningResultJson.Create(item, Identity(item), good, "not retained")).RawReplyPreview);
        Assert.ThrowsExactly<ArgumentException>(() => TuningResultJson.Write(item, result with { Score = TuningScore.Correct }));
    }

    [TestMethod]
    public void ScriptedPerfectModelsBeatBaselinesWhileAbstentionAndAlwaysConsistentFail()
    {
        foreach (var group in Corpus.Cases.GroupBy(c => c.Case.Role))
        {
            var cases = group.Select(c => c.Case with { Review = new(LabelReviewState.Approved, 1) }).ToArray();
            var observations = cases.SelectMany(item => Enumerable.Range(0, 2).Select(repeat =>
                new CaseObservation(TuningCaseIdentity.From(item), repeat, TuningScoring.Score(item, TuningCaseRenderer.Render(item),
                    Completion(group.Single(c => c.Case.Id == item.Id).ReferenceReplies[0]))))).ToArray();
            var summary = TuningMetrics.Summarize(cases, observations, 2, Key);
            var calibration = new FrozenRoleCalibration(Key, new(.8m, .1m, .03m), true);
            Assert.AreEqual(1m, summary.TaskScore); Assert.AreEqual(1m, summary.Stability);
            Assert.AreEqual(TuningAdequacy.Adequate, TuningAdequacyRules.Assess(summary, calibration));
            Assert.AreEqual(TuningAdequacy.Provisional, TuningAdequacyRules.Assess(summary, null));
            Assert.AreEqual(TuningAdequacy.Provisional, TuningAdequacyRules.Assess(summary, calibration with { IsFrozen = false }));
            Assert.AreEqual(TuningAdequacy.BelowThreshold, TuningAdequacyRules.Assess(summary with { Correct = (int)(cases.Length * 2 * summary.Baseline), Incorrect = cases.Length * 2 - (int)(cases.Length * 2 * summary.Baseline) }, calibration));
            Assert.AreEqual(TuningAdequacy.Disqualified, TuningAdequacyRules.Assess(summary with { HasDisqualifier = true }, calibration));
            Assert.AreEqual(TuningAdequacy.Disqualified, TuningAdequacyRules.Assess(summary with { Unknown = 1 }, calibration));
            Assert.AreEqual(TuningAdequacy.BelowThreshold, TuningAdequacyRules.Assess(summary with { Validity = .94m }, calibration));
            Assert.AreEqual(TuningAdequacy.BelowThreshold, TuningAdequacyRules.Assess(summary with { Stability = .89m }, calibration));
            Assert.AreEqual(TuningAdequacy.BelowThreshold, TuningAdequacyRules.Assess(summary with { Correct = cases.Length * 14 / 10, Incorrect = cases.Length * 6 / 10 }, calibration));
            Assert.AreEqual(TuningAdequacy.BelowThreshold, TuningAdequacyRules.Assess(summary with { Baseline = .75m, Correct = cases.Length * 16 / 10, Incorrect = cases.Length * 4 / 10 }, calibration));
            Assert.AreEqual(TuningAdequacy.Incomplete, TuningAdequacyRules.Assess(summary with { LabelsApproved = false }, calibration));
            Assert.AreEqual(TuningAdequacy.Incomplete, TuningAdequacyRules.Assess(summary, calibration with { MinimumCases = 100 }));
            Assert.AreEqual(TuningAdequacy.NotApplicable, TuningAdequacyRules.Assess(summary with { Source = TuningSource.Household }, calibration with { Identity = Key with { CorpusVersion = "other" } }));
            Assert.ThrowsExactly<ArgumentException>(() => TuningAdequacyRules.Assess(summary, calibration with { Identity = Key with { CorpusVersion = "other" } }));
            Assert.ThrowsExactly<ArgumentException>(() => TuningAdequacyRules.Assess(summary, calibration with { Threshold = new(.8m, 0, 0) }));
        }
        foreach (var role in new[] { ModelRole.ArtifactValidationAssessment, ModelRole.CandidateAmbiguityAssessment })
        {
            var correct = Corpus.Cases.Where(c => c.Case.Role == role).Count(fixture =>
            {
                var rendered = TuningCaseRenderer.Render(fixture.Case);
                // A syntactically compliant positive answer ignores the negative/unknown cases.
                var reply = "{\"schema\":\"" + (role == ModelRole.ArtifactValidationAssessment ? "artifact-assessment/1" : "candidate-assessment/1") + "\",\"verdict\":\"consistent\",\"claims\":[{\"kind\":\"" +
                    (role == ModelRole.ArtifactValidationAssessment ? "supports_declared_identity" : "supports_same_work") + "\",\"evidence_ids\":[\"" + (rendered.Findings.FirstOrDefault(f => f.Polarity == FamilyLibrarian.Domain.Evidence.EvidencePolarity.Positive)?.Id ?? "absent") + "\"],\"note\":\"Consistent.\"}]}";
                return TuningScoring.Score(fixture.Case, rendered, Completion(reply)).Score == TuningScore.Correct;
            });
            Assert.IsTrue(correct <= 24, role.ToString());
        }
    }

    [TestMethod]
    public void MissingCellsStayInTheDenominatorAndQuickCannotMeasureStability()
    {
        var item = Case("II-01"); var score = new CaseScore(TuningScore.Correct, true, false, item.Expected.Acceptable[0]);
        TuningCase[] cases = [item]; CaseObservation[] cells = [new(TuningCaseIdentity.From(item), 0, score)];
        var partial = TuningMetrics.Summarize(cases, cells, 2, Key);
        Assert.AreEqual(.5m, partial.TaskScore); Assert.AreEqual(.5m, partial.Validity); Assert.AreEqual(0m, partial.Stability);
        Assert.AreEqual(TuningAdequacy.Incomplete, TuningAdequacyRules.Assess(partial, new(Key, new(.8m, .1m, .03m), true)));
        var quick = TuningMetrics.Summarize(cases, cells, 1, Key);
        Assert.IsNull(quick.Stability); Assert.AreEqual(TuningAdequacy.Provisional, TuningAdequacyRules.Assess(quick, new(Key, new(.8m, .1m, .03m), true)));
        Assert.ThrowsExactly<ArgumentException>(() => TuningMetrics.Summarize(cases, [cells[0], cells[0]], 1, Key));
    }

    [TestMethod]
    public void UntestedModesAreAbsentAndOnlyCompleteBestCoverageCanClaimBest()
    {
        ModelRole[] roles = [ModelRole.IntentInterpretation]; TuningModelKey[] models = [Model];
        var rows = Enum.GetValues<ModelReasoningMode>().Select(mode => new OptionCoverage(new(roles[0], Model, mode),
            CapabilitySupport.Supported, OptionEvaluation.Completed, null, 1, false, true, 3)).ToArray();
        Assert.IsTrue(TuningComparison.Assess(TuningGoal.Best, roles, models, rows).CanClaimBest);
        Assert.IsFalse(TuningComparison.Assess(TuningGoal.GoodQuickly, roles, models, rows.Select(r => r with { Repeats = 2 }).ToArray()).CanClaimBest);
        Assert.IsFalse(TuningComparison.Assess(TuningGoal.Best, roles, models, rows[..1]).CanClaimBest);
        var notTested = rows[2] with { Evaluation = OptionEvaluation.NotTested, TaskScore = null, Omission = OptionOmission.DefaultSufficient };
        var coverage = TuningComparison.Assess(TuningGoal.Best, roles, models, [rows[0], rows[1], notTested]);
        Assert.IsFalse(coverage.CanClaimBest); Assert.HasCount(1, coverage.Untested);
        Assert.ThrowsExactly<ArgumentException>(() => TuningComparison.Assess(TuningGoal.Best, roles, models, [notTested with { TaskScore = 0 }]));
        Assert.IsTrue(TuningComparison.Assess(TuningGoal.Best, roles, models, [rows[0], rows[1], rows[2] with { Evaluation = OptionEvaluation.Disqualified, ObservedTerminalFailure = true, TaskScore = null }]).CanClaimBest);
        Assert.IsFalse(TuningComparison.Assess(TuningGoal.Best, roles, models, [rows[0], rows[1], rows[2] with { Evaluation = OptionEvaluation.Partial }]).CanClaimBest);
        Assert.IsFalse(TuningComparison.Assess(TuningGoal.Best, roles, models, [rows[0], rows[1], rows[2] with { ComparableIdentity = false }]).CanClaimBest);
        Assert.IsFalse(TuningComparison.Assess(TuningGoal.Best, roles, models, rows.Select(r => r with { Repeats = 2 }).ToArray()).CanClaimBest);
        Assert.IsTrue(TuningComparison.Assess(TuningGoal.Best, roles, models, [rows[0], rows[1], rows[2] with { Support = CapabilitySupport.Unsupported, TaskScore = null }]).CanClaimBest);
        Assert.AreEqual(2, TuningComparison.Repeats(TuningComparison.Tier(TuningGoal.GoodQuickly)));
        Assert.AreEqual(3, TuningComparison.Repeats(TuningComparison.Tier(TuningGoal.Best)));
        Assert.AreEqual(1, TuningComparison.Repeats(TuningTier.Quick));
        Assert.AreNotEqual(Model, new TuningModelKey("other-connection", Model.Alias));
    }

    private sealed class TriageExecution(string reply) : IModelRoleResolver, IModelExecutionPolicy, IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public int Resolutions { get; private set; }
        public int Calls { get; private set; }
        public ModelExecutionRequest? Request { get; private set; }
        public Task<ModelRoleResolution> ResolveAsync(ModelRole role, CancellationToken cancellationToken)
        {
            Resolutions++;
            return Task.FromResult(new ModelRoleResolution(role, ModelUnavailableReason.None, new Uri("http://model.test"),
                null, "scripted", false, ModelExecutionLimits.Default));
        }
        public Task<ModelExecution<T>> ExecuteAsync<T>(ModelExecutionRequest request, Func<string, ModelParseResult<T>> parse, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            var parsed = parse(reply);
            return Task.FromResult(new ModelExecution<T>(parsed.Failure, parsed.Value, parsed.Detail, null, 0, "scripted", false));
        }
    }

    private sealed class CapturingPolicy : IModelExecutionPolicy
    {
        public ModelExecutionRequest? Request { get; set; }
        public Task<ModelExecution<T>> ExecuteAsync<T>(ModelExecutionRequest request, Func<string, ModelParseResult<T>> parse, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ModelExecution<T>(ModelCallOutcome.EmptyContent, default, null, null, 0, "scripted", false));
        }
    }
}
