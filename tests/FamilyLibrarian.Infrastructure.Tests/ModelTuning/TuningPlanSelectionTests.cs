using System.Text.Json;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTuning;

[TestClass]
public sealed class TuningPlanSelectionTests
{
    private static readonly BuiltInTuningCorpus Corpus = BuiltInTuningCorpus.LoadEmbedded();
    private static readonly ModelRole[] Primary = [ModelRole.ArtifactValidationAssessment, ModelRole.CandidateAmbiguityAssessment, ModelRole.IntentInterpretation];
    private static readonly string[] RequestedAuthorEvidence = ["req-author"];
    private static TuningModelKey Model(int index) => new("test-local", "model-" + index);
    private static TuningRolePlanScope Scope(ModelRole role)
    {
        var cases = Corpus.Cases.Where(c => c.Case.Role == role).Select(c => c.Case).ToArray();
        var key = Corpus.CalibrationKey(role);
        return new(role, cases, key, new(key, new(role == ModelRole.IntentInterpretation ? .9m : .8m, .2m, .1m), true));
    }

    private static TestedTuningPlanOption Option(ModelRole role, int model, decimal score = 1,
        TuningPlacement placement = TuningPlacement.Local, ModelReasoningMode mode = ModelReasoningMode.Inherit)
    {
        var scope = Scope(role); var total = scope.Cases.Count * 3; var correct = (int)(score * total);
        return new(new(role, Model(model), mode), placement,
            new(TuningSource.BuiltIn, scope.Identity, scope.Cases.Count, 3, correct, total - correct, 0, 1, 1,
                TuningScoring.AbstainBaseline(scope.Cases), true, true, false, 1, null, null, new Dictionary<string, int>()), new());
    }

    private static TuningPlanInput Input(IReadOnlyList<TestedTuningPlanOption> options, ModelRole[]? roles = null, TuningModelKey[]? models = null)
    {
        roles ??= options.Select(c => c.Option.Role).Distinct().ToArray();
        models ??= options.Select(c => c.Option.Model).Distinct().ToArray();
        var coverage = (from role in roles.Where(TuningRoles.IsScored)
                        from model in models
                        from mode in Enum.GetValues<ModelReasoningMode>()
                        let candidate = options.SingleOrDefault(c => c.Option == new TuningOptionKey(role, model, mode))
                        select new OptionCoverage(new(role, model, mode), CapabilitySupport.Supported,
                            candidate is null ? OptionEvaluation.NotTested : OptionEvaluation.Completed,
                            candidate is null ? OptionOmission.NotSelectedForEvaluation : null,
                            candidate?.BuiltInScore.TaskScore, false, true, 3)).ToArray();
        return new(TuningGoal.Best, roles, roles.Where(TuningRoles.IsScored).Select(Scope).ToArray(), models, options,
            models.Select((m, rank) => new AdministratorModelOrder(m, rank)).ToArray(), [], coverage, []);
    }

    [TestMethod]
    public void NoiseIsInclusiveAnchoredToTheMaximumAndNeverChained()
    {
        var role = Primary[0];
        var input = Input([Option(role, 0, .8m), Option(role, 1, .9m), Option(role, 2)]);
        foreach (var plan in new[] { TuningPlanSelection.SelectBestForEachTaskPlan(input), TuningPlanSelection.SelectFewestModelsPlan(input) })
        {
            var chosen = plan.Assignments.Single();
            Assert.AreEqual(Model(1), chosen.Option.Model);
            Assert.AreEqual(.9m, chosen.Score); Assert.AreEqual(1m, chosen.BestAdmissibleScore); Assert.AreEqual(.1m, chosen.ScoreGap);
        }
    }

    [TestMethod]
    public void FewestUsesOneSameBandModelRatherThanSixIndependentWinners()
    {
        var roles = TuningRoles.Scored.ToArray();
        var options = roles.Select((r, i) => Option(r, i)).Concat(roles.Select(r => Option(r, 6, .9m))).ToArray();
        var input = Input(options, roles);
        Assert.AreEqual(6, TuningPlanSelection.CountDistinctLocalModels(TuningPlanSelection.SelectBestForEachTaskPlan(input)));
        var fewest = TuningPlanSelection.SelectFewestModelsPlan(input);
        Assert.AreEqual(TuningPlanStatus.Complete, fewest.Status);
        Assert.IsTrue(fewest.Assignments.All(a => a.Option.Model == Model(6)));
        Assert.AreEqual(1, TuningPlanSelection.CountDistinctLocalModels(fewest));
        var outside = options.Select(o => o.Option.Model != Model(6) ? o : o with
        { BuiltInScore = o.BuiltInScore with { Correct = o.BuiltInScore.CaseCount * 3 * 8 / 10, Incorrect = o.BuiltInScore.CaseCount * 3 * 2 / 10 } }).ToArray();
        Assert.AreEqual(6, TuningPlanSelection.CountDistinctLocalModels(TuningPlanSelection.SelectFewestModelsPlan(Input(outside, roles))));
    }

    [TestMethod]
    public void ExactCoverMatchesEverySubsetAtTheSixRoleSixteenModelBound()
    {
        var roles = TuningRoles.Scored.ToArray();
        int[] masks = [1, 2, 4, 8, 16, 32, 3, 6, 12, 24, 48, 33, 5, 18, 40, 21];
        var options = (from model in Enumerable.Range(0, 16) from bit in Enumerable.Range(0, 6)
                       where (masks[model] & 1 << bit) != 0 select Option(roles[bit], model)).ToArray();
        var input = Input(options, roles, Enumerable.Range(0, 16).Select(Model).ToArray());
        var covers = new List<int[]>();
        for (var subset = 1; subset < 65536; subset++)
        {
            var members = Enumerable.Range(0, 16).Where(i => (subset & 1 << i) != 0).ToArray();
            if (members.Select(i => masks[i]).Aggregate(0, (a, b) => a | b) == 63) covers.Add(members);
        }
        var expected = covers.OrderBy(c => c.Length).ThenBy(c => string.Join(',', c.Select(i => i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))), StringComparer.Ordinal).First();
        var result = TuningPlanSelection.SelectFewestModelsPlan(input);
        CollectionAssert.AreEquivalent(expected.Select(Model).ToArray(), result.Assignments.Select(a => a.Option.Model).Distinct().ToArray());
        var reversed = input with { Models = input.Models.Reverse().ToArray(), Roles = input.Roles.Reverse().ToArray(),
            TestedOptions = input.TestedOptions.Reverse().ToArray(), AdministratorOrder = input.AdministratorOrder.Reverse().ToArray(), Coverage = input.Coverage.Reverse().ToArray() };
        CollectionAssert.AreEqual(result.Assignments.ToArray(), TuningPlanSelection.SelectFewestModelsPlan(reversed).Assignments.ToArray());
    }

    [TestMethod]
    public void EqualSizeCoversUseLexicographicRankVectorsRatherThanNumericMasks()
    {
        TestedTuningPlanOption[] options = [Option(Primary[0], 0), Option(Primary[0], 1), Option(Primary[1], 1),
            Option(Primary[2], 2), Option(Primary[1], 3), Option(Primary[2], 3)];
        var input = Input(options, Primary, Enumerable.Range(0, 4).Select(Model).ToArray());
        var plan = TuningPlanSelection.SelectFewestModelsPlan(input);
        CollectionAssert.AreEquivalent(new[] { Model(0), Model(3) }, plan.Assignments.Select(a => a.Option.Model).Distinct().ToArray());
    }

    [TestMethod]
    public void LocalFirstAndExplicitOrderBoundCloudOverrideAreDifferentFromTestingEligibility()
    {
        var role = Primary[0];
        var input = Input([Option(role, 0, 1, TuningPlacement.Cloud), Option(role, 1, .9m), Option(role, 2, 1, TuningPlacement.Cloud)]);
        Assert.AreEqual(Model(1), TuningPlanSelection.SelectBestForEachTaskPlan(input).Assignments.Single().Option.Model);
        var identity = TuningPlanSelection.AdministratorOrderIdentity(input.AdministratorOrder);
        var preference = new CloudPreferenceOverride(role, Model(0), identity);
        var explicitCloud = input with { CloudPreferences = [preference] };
        var chosen = TuningPlanSelection.SelectFewestModelsPlan(explicitCloud).Assignments.Single();
        Assert.AreEqual(Model(0), chosen.Option.Model);
        Assert.AreEqual(0, TuningPlanSelection.CountDistinctLocalModels(TuningPlanSelection.SelectFewestModelsPlan(explicitCloud)));
        Assert.ThrowsExactly<ArgumentException>(() => TuningPlanSelection.SelectBestForEachTaskPlan(input with
        { CloudPreferences = [preference with { AdministratorOrderIdentity = "old-order" }] }));
        var localFirstOrder = input.AdministratorOrder.Select(r => r with { Rank = r.Rank == 0 ? 1 : r.Rank == 1 ? 0 : r.Rank }).ToArray();
        Assert.ThrowsExactly<ArgumentException>(() => TuningPlanSelection.SelectBestForEachTaskPlan(input with
        { AdministratorOrder = localFirstOrder, CloudPreferences = [preference with { AdministratorOrderIdentity = TuningPlanSelection.AdministratorOrderIdentity(localFirstOrder) }] }));
        var unknown = Input([Option(role, 0, 1, TuningPlacement.Unknown)]);
        Assert.AreEqual(TuningPlacement.Unknown, TuningPlanSelection.SelectBestForEachTaskPlan(unknown).Assignments.Single().Placement);
        Assert.AreEqual(0, TuningPlanSelection.CountDistinctLocalModels(TuningPlanSelection.SelectBestForEachTaskPlan(unknown)));
    }

    [TestMethod]
    public void SameModelModeTiesUseKnownMetricsAndCostOnlyForTheEntireComparableGroup()
    {
        var role = Primary[0];
        var options = Enum.GetValues<ModelReasoningMode>().Select(mode => Option(role, 0, 1, mode: mode)).ToArray();
        var input = Input(options);
        Assert.IsTrue(TuningPlanSelection.SelectBestForEachTaskPlan(input).Coverage.CanClaimBest);
        Assert.AreEqual(ModelReasoningMode.Inherit, TuningPlanSelection.SelectBestForEachTaskPlan(input).Assignments.Single().Option.Reasoning);
        var known = options.Select(o => o.Option.Reasoning == ModelReasoningMode.Disabled ? o with { Measurements = new(10) } : o).ToArray();
        Assert.AreEqual(ModelReasoningMode.Disabled, TuningPlanSelection.SelectBestForEachTaskPlan(Input(known)).Assignments.Single().Option.Reasoning);
        var tokens = options.Select(o => o with { Measurements = new(10, o.Option.Reasoning == ModelReasoningMode.Low ? 1 : null) }).ToArray();
        Assert.AreEqual(ModelReasoningMode.Low, TuningPlanSelection.SelectBestForEachTaskPlan(Input(tokens)).Assignments.Single().Option.Reasoning);
        var costs = options.Select(o => o with { Measurements = new(10, 10, o.Option.Reasoning == ModelReasoningMode.Disabled ? 1 : 2, "USD") }).ToArray();
        Assert.AreEqual(ModelReasoningMode.Disabled, TuningPlanSelection.SelectBestForEachTaskPlan(Input(costs)).Assignments.Single().Option.Reasoning);
        var incomparable = costs.Select(o => o.Option.Reasoning == ModelReasoningMode.Low ? o with { Measurements = new(10, 10) } : o).ToArray();
        foreach (var permutation in new[] { incomparable, incomparable.Reverse().ToArray(), new[] { incomparable[1], incomparable[2], incomparable[0] } })
            Assert.AreEqual(ModelReasoningMode.Inherit, TuningPlanSelection.SelectBestForEachTaskPlan(Input(permutation)).Assignments.Single().Option.Reasoning);
        var mixedUnits = costs.Select(o => o.Option.Reasoning == ModelReasoningMode.Low ? o with { Measurements = new(10, 10, 0, "EUR") } : o).ToArray();
        Assert.AreEqual(ModelReasoningMode.Inherit, TuningPlanSelection.SelectBestForEachTaskPlan(Input(mixedUnits)).Assignments.Single().Option.Reasoning);
    }

    [TestMethod]
    public void UncoveredUnassessableAndUnscoredRolesAreReportedWithoutInventingACompletePlan()
    {
        var input = Input([Option(Primary[0], 0)], Primary);
        foreach (var plan in new[] { TuningPlanSelection.SelectBestForEachTaskPlan(input), TuningPlanSelection.SelectFewestModelsPlan(input) })
        {
            Assert.AreEqual(TuningPlanStatus.NoCompletePlan, plan.Status); Assert.HasCount(2, plan.UncoveredRoles);
            Assert.HasCount(1, plan.Assignments); Assert.IsFalse(plan.Coverage.CanClaimBest);
        }
        var noRoles = Input([], [ModelRole.ConversationResponse, ModelRole.VisionCoverIdentification]);
        var empty = TuningPlanSelection.SelectFewestModelsPlan(noRoles);
        Assert.AreEqual(TuningPlanStatus.NoRolesSelected, empty.Status); Assert.HasCount(2, empty.UnscoredRoles);
        Assert.AreEqual(0, TuningPlanSelection.CountDistinctLocalModels(empty));
        var unassessable = input with { Roles = input.Roles.Select(r => r with { Calibration = null }).ToArray() };
        var rejected = TuningPlanSelection.SelectBestForEachTaskPlan(unassessable);
        Assert.HasCount(3, rejected.NotAssessableRoles); Assert.HasCount(0, rejected.Assignments);
    }

    [TestMethod]
    public void DistinctLocalCounterUsesConnectionIdentityAndIgnoresOtherPlacementsOrInadequateAssignments()
    {
        var key = new TuningModelKey("first", "same-alias"); var other = key with { ConnectionId = "second" };
        RolePlanOption Assignment(TuningModelKey model, TuningPlacement placement, ModelReasoningMode mode = ModelReasoningMode.Inherit,
            TuningAdequacy adequacy = TuningAdequacy.Adequate) => new(new(Primary[0], model, mode), placement, 1, 1, .1m, adequacy);
        var plan = new TuningPlan(TuningPlanKind.BestForEachTask, TuningPlanStatus.Complete,
            [Assignment(key, TuningPlacement.Local), Assignment(key, TuningPlacement.Local, ModelReasoningMode.Disabled), Assignment(other, TuningPlacement.Local),
             Assignment(Model(3), TuningPlacement.Cloud), Assignment(Model(4), TuningPlacement.Unknown),
             Assignment(Model(5), TuningPlacement.Local, adequacy: TuningAdequacy.NotApplicable)], [], new(false, [], [], [], []), [], [], []);
        Assert.AreEqual(2, TuningPlanSelection.CountDistinctLocalModels(plan));
    }

    [TestMethod]
    public void HouseholdEvidenceIsReportedButCannotRescueOrDemoteBuiltInOptions()
    {
        var role = Primary[0]; var input = Input([Option(role, 0, .8m), Option(role, 1)]);
        ReportedHouseholdEvidence[] household = [new(role, Model(0), 1, 200, 0, "household-a"), new(role, Model(1), 0, 200, 0, "household-a")];
        var plan = TuningPlanSelection.SelectFewestModelsPlan(input with { HouseholdEvidence = household });
        Assert.AreEqual(Model(1), plan.Assignments.Single().Option.Model);
        CollectionAssert.AreEqual(household, plan.HouseholdEvidence.ToArray());
    }

    [TestMethod]
    public void InvalidIdentitySourceOrderPlacementAndFabricatedCoverageAreRejected()
    {
        var option = Option(Primary[0], 0); var input = Input([option]);
        void Reject(TuningPlanInput changed) => Assert.ThrowsExactly<ArgumentException>(() => TuningPlanSelection.SelectFewestModelsPlan(changed));
        Reject(input with { Models = [Model(0), Model(0)] });
        Reject(input with { AdministratorOrder = [] });
        Reject(input with { Roles = [input.Roles[0] with { Identity = input.Roles[0].Identity with { LabelVersion = "old" } }] });
        Reject(input with { Roles = [input.Roles[0] with { Calibration = input.Roles[0].Calibration! with { Identity = input.Roles[0].Identity with { LabelVersion = "old" } } }] });
        Reject(input with { TestedOptions = [option with { BuiltInScore = option.BuiltInScore with { Source = TuningSource.Household } }] });
        Reject(input with { TestedOptions = [option with { BuiltInScore = option.BuiltInScore with { Identity = option.BuiltInScore.Identity with { CorpusVersion = "old" } } }] });
        Reject(input with { TestedOptions = [option with { BuiltInScore = option.BuiltInScore with { Identity = option.BuiltInScore.Identity with { CorpusVersion = "old" } } }],
            Coverage = input.Coverage.Select(c => c.Option == option.Option ? c with { Evaluation = OptionEvaluation.Partial } : c).ToArray() });
        Reject(input with { TestedOptions = [option with { BuiltInScore = option.BuiltInScore with { Incorrect = 1 } }] });
        var another = Option(Primary[0], 0, mode: ModelReasoningMode.Disabled) with { Placement = TuningPlacement.Cloud };
        Reject(Input([option, another]));
        Reject(input with { Coverage = input.Coverage.Select(c => c.Option == option.Option ? c with { TaskScore = .5m } : c).ToArray() });
        Reject(input with { Coverage = input.Coverage.Select(c => c.Option == option.Option ? c with { Evaluation = OptionEvaluation.NotTested, TaskScore = null } : c).ToArray() });
        Reject(input with { Models = Enumerable.Range(0, 17).Select(Model).ToArray() });
        Reject(input with { Roles = [input.Roles[0] with { Cases = input.Roles[0].Cases.Select(c => c with { Source = TuningSource.Household }).ToArray() }] });
    }

    [TestMethod]
    public void InadequatePartialDisqualifiedAndUnfrozenOptionsNeverCoverARole()
    {
        var option = Option(Primary[0], 0); var input = Input([option]);
        foreach (var score in new[] { option.BuiltInScore with { Validity = .94m }, option.BuiltInScore with { Stability = .89m },
            option.BuiltInScore with { HasDisqualifier = true }, option.BuiltInScore with { Unknown = 1, Correct = 119 } })
        {
            var changed = input with { TestedOptions = [option with { BuiltInScore = score }], Coverage = input.Coverage.Select(c =>
                c.Option == option.Option ? c with { TaskScore = score.TaskScore } : c).ToArray() };
            Assert.AreEqual(TuningPlanStatus.NoCompletePlan, TuningPlanSelection.SelectFewestModelsPlan(changed).Status);
        }
        var unfrozen = input with { Roles = [input.Roles[0] with { Calibration = input.Roles[0].Calibration! with { IsFrozen = false } }] };
        Assert.AreEqual(TuningPlanStatus.NoCompletePlan, TuningPlanSelection.SelectBestForEachTaskPlan(unfrozen).Status);
        var partial = input with { Coverage = input.Coverage.Select(c => c.Option == option.Option ? c with { Evaluation = OptionEvaluation.Partial } : c).ToArray() };
        Assert.AreEqual(TuningPlanStatus.NoCompletePlan, TuningPlanSelection.SelectBestForEachTaskPlan(partial).Status);
    }

    [TestMethod]
    public void TerminalExclusionMayRetainFailedEvidenceWithoutFabricatingACoverageScore()
    {
        var failed = Option(Primary[0], 0);
        failed = failed with { BuiltInScore = failed.BuiltInScore with { Correct = 0, Incorrect = 120, Validity = 0,
            Stability = 0, HasDisqualifier = true, IsComplete = false } };
        var input = Input([failed, Option(Primary[0], 1)]);
        input = input with { Coverage = input.Coverage.Select(c => c.Option == failed.Option
            ? c with { Evaluation = OptionEvaluation.Disqualified, ObservedTerminalFailure = true, TaskScore = null }
            : c.Evaluation == OptionEvaluation.NotTested ? c with { Support = CapabilitySupport.Unsupported } : c).ToArray() };
        var plan = TuningPlanSelection.SelectBestForEachTaskPlan(input);
        Assert.AreEqual(Model(1), plan.Assignments.Single().Option.Model);
        Assert.IsTrue(plan.Coverage.CanClaimBest);
        Assert.IsNull(input.Coverage.Single(c => c.Option == failed.Option).TaskScore);
    }

    [TestMethod]
    public void FrozenProfilePinsOnlyThreeRolesAndDistinguishesPriorNoiseFromMeasuredRepeatRange()
    {
        var manifest = BuiltInTuningCalibration.LoadEmbedded(Corpus);
        Assert.AreEqual("synthetic-5", manifest.CorpusVersion); Assert.AreEqual("owner-reviewed-4", manifest.LabelVersion);
        foreach (var entry in manifest.Roles)
        {
            if (!Primary.Contains(entry.Role)) { Assert.IsNull(entry.Calibration); continue; }
            var calibration = entry.Calibration!;
            Assert.AreEqual(Corpus.CalibrationKey(entry.Role), calibration.Identity);
            Assert.IsTrue(calibration.IsFrozen); Assert.AreEqual(TuningNoiseBasis.Prior, calibration.NoiseBasis);
            Assert.AreEqual(0m, calibration.MeasuredRepeatRange); Assert.AreEqual(.1m, calibration.Threshold.NoiseBand);
            Assert.AreEqual(.2m, calibration.Threshold.BaselineMargin); Assert.AreEqual(.95m, calibration.MinimumValidity); Assert.AreEqual(.9m, calibration.MinimumAgreement);
            Assert.AreEqual(entry.Role == ModelRole.IntentInterpretation ? .9m : .8m, calibration.Threshold.AbsoluteFloor);
        }
        Assert.AreEqual(TuningRoleAssessability.NotAssessable, manifest.Roles.Single(r => r.Role == ModelRole.ArtifactValidationDecision).Status);
        Assert.AreEqual(TuningRoleAssessability.NotAssessable, manifest.Roles.Single(r => r.Role == ModelRole.SearchGuidance).Status);
        Assert.AreEqual(TuningRoleAssessability.NotScorable, manifest.Roles.Single(r => r.Role == ModelRole.CandidateTriage).Status);
        Assert.ThrowsExactly<JsonException>(() => BuiltInTuningCalibration.Load(Corpus,
            JsonSerializer.Serialize(manifest with { CorpusVersion = "old" }, TuningInputJson.Options)));
        var first = manifest.Roles.First(r => r.Calibration is not null);
        var stale = manifest with { Roles = manifest.Roles.Select(r => r == first ? r with
        { Calibration = r.Calibration! with { Identity = r.Calibration.Identity with { PromptParserRulesScorerIdentity = "stale" } } } : r).ToArray() };
        Assert.ThrowsExactly<ArgumentException>(() => BuiltInTuningCalibration.Load(Corpus, JsonSerializer.Serialize(stale, TuningInputJson.Options)));
    }

    [TestMethod]
    public void ThePreWordingIntentIdentityCannotCertifyTheNewPrompt()
    {
        const string priorIdentity = "d65808b1ecafd95fa1660bbebadacd8d015bca3ae082bfc9f0a20a93eafb66e1";
        var manifest = BuiltInTuningCalibration.LoadEmbedded(Corpus);
        var intent = manifest.Roles.Single(r => r.Role == ModelRole.IntentInterpretation);
        Assert.AreNotEqual(priorIdentity, intent.Calibration!.Identity.PromptParserRulesScorerIdentity);
        var stale = manifest with { Roles = manifest.Roles.Select(r => r == intent ? r with
        {
            Calibration = r.Calibration! with { Identity = r.Calibration.Identity with { PromptParserRulesScorerIdentity = priorIdentity } }
        } : r).ToArray() };
        Assert.ThrowsExactly<ArgumentException>(() => BuiltInTuningCalibration.Load(Corpus,
            JsonSerializer.Serialize(stale, TuningInputJson.Options)));
    }

    [TestMethod]
    public void B1AcceptsOnlyRequiredHintPlusTheExactOptionalSuppliedCanonicalSeries()
    {
        string[] ids = ["SG-01", "SG-02", "SG-04", "SG-05", "SG-06", "SG-07", "SG-08", "SG-09", "SG-10", "SG-11",
            "SG-16", "SG-17", "SG-18", "SG-19", "SG-20", "SG-21", "SG-22", "SG-23"];
        var witnesses = 0;
        foreach (var fixture in Corpus.Cases.Where(f => ids.Contains(f.Case.Id, StringComparer.Ordinal)))
        {
            var item = fixture.Case; var rendered = TuningCaseRenderer.Render(item);
            foreach (var alternative in item.Expected.Acceptable)
            {
                Assert.HasCount(2, alternative.AllowedSuggestions!); Assert.HasCount(1, alternative.RequiredSuggestions!);
                Assert.Contains(new TuningSearchName("series_naming", "The Lantern Ledger"), alternative.AllowedSuggestions!);
            }
            foreach (var reply in fixture.ReferenceReplies)
                Assert.AreEqual(TuningScore.Correct, TuningScoring.Score(item, rendered, new(ModelCallOutcome.Succeeded, reply)).Score);
            witnesses += fixture.ReferenceReplies.Count - item.Expected.Acceptable.Count;
            var optionalOnly = TuningReplies.Render(item.Role, new(GiveUp: false, AllowedSuggestions: [new("series_naming", "The Lantern Ledger")]), rendered);
            Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered, new(ModelCallOutcome.Succeeded, optionalOnly)).Score);
            var invented = JsonNode.Parse(fixture.ReferenceReplies[0])!;
            invented["suggestions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { reason = "author_variant", value = "N. J. Bellwick", evidence_ids = RequestedAuthorEvidence, note = "Unsupported initial." }));
            Assert.AreEqual(TuningScore.Incorrect, TuningScoring.Score(item, rendered, new(ModelCallOutcome.Succeeded, invented.ToJsonString())).Score);
        }
        Assert.AreEqual(22, witnesses);
        Assert.AreEqual(365, Corpus.Cases.Sum(c => c.ReferenceReplies.Count));
    }
}
