using FamilyLibrarian.Application.DecisionCapture;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Application.ModelTuning;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelGateway;

internal sealed class FakeCaptureStore : IDecisionCaptureStore
{
    public DecisionCaptureRetentionCounts Counts { get; set; } = new(0, 0, 0);

    public DecisionCapturePurgeResult Purged { get; set; } = new(3, 5, 2, 1);

    public List<ModelRole?> Purges { get; } = [];

    public int Previews { get; private set; }

    public Task<DecisionCaptureInsight> GetInsightAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult(new DecisionCaptureInsight(nowUtc, [], [], 0));

    public Task<DecisionCaptureRetentionCounts> CountRetentionAsync(DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        Previews++;
        return Task.FromResult(Counts);
    }

    public Task<DecisionCaptureRetentionCounts> RunRetentionAsync(DecisionCaptureLimits limits, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Counts);

    public Task<DecisionCapturePurgeResult> PurgeAsync(ModelRole? role, CancellationToken cancellationToken)
    {
        Purges.Add(role);
        return Task.FromResult(Purged);
    }
}

/// <summary>A6-TUNER-C1-a: the limits, the switches, the administrator-only service and the run identity that capture will record.</summary>
[TestClass]
public sealed class DecisionCaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static (DecisionCaptureService Service, FakeGatewayStore Gateway, FakeCaptureStore Store, CapturingAudit Audit) Create(bool admin = true)
    {
        var gateway = new FakeGatewayStore();
        var store = new FakeCaptureStore();
        var audit = new CapturingAudit();
        var service = new DecisionCaptureService(gateway, store, audit, new FakeUser(admin ? Guid.NewGuid() : null), new FakeAdmin(admin), new FakeClock());
        return (service, gateway, store, audit);
    }

    [TestMethod]
    public void DefaultsAreTheOwnersRecommendationsAndAreValid()
    {
        var limits = DecisionCaptureLimits.Default;

        Assert.AreEqual(200, limits.SnapshotsPerRole);
        Assert.AreEqual(180, limits.UnlabeledSnapshotDays);
        Assert.IsNull(limits.LabeledSnapshotDays, "Labeled snapshots are kept until the cap or a purge by default.");
        Assert.AreEqual(10, limits.CallsPerSnapshot);
        Assert.AreEqual(180, limits.FindingsDays);
        Assert.IsNull(limits.Validate());
        Assert.AreEqual(32_768, DecisionCaptureLimits.MaxSnapshotBytes);
    }

    [TestMethod]
    [DataRow(49, false)]
    [DataRow(50, true)]
    [DataRow(1_000, true)]
    [DataRow(1_001, false)]
    public void SnapshotsPerRoleEnforcesItsCeilingAndFloor(int value, bool valid) =>
        Assert.AreEqual(valid, (DecisionCaptureLimits.Default with { SnapshotsPerRole = value }).Validate() is null);

    [TestMethod]
    [DataRow(29, false)]
    [DataRow(30, true)]
    [DataRow(730, true)]
    [DataRow(731, false)]
    public void UnlabeledDaysEnforceTheirRange(int value, bool valid) =>
        Assert.AreEqual(valid, (DecisionCaptureLimits.Default with { UnlabeledSnapshotDays = value }).Validate() is null);

    [TestMethod]
    [DataRow(null, true)]
    [DataRow(89, false)]
    [DataRow(90, true)]
    [DataRow(1_825, true)]
    [DataRow(1_826, false)]
    [DataRow(0, false)]
    [DataRow(-1, false)]
    public void LabeledDaysAreNoneOrWithinTheirRange(int? value, bool valid) =>
        Assert.AreEqual(valid, (DecisionCaptureLimits.Default with { LabeledSnapshotDays = value }).Validate() is null);

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, true)]
    [DataRow(20, true)]
    [DataRow(21, false)]
    public void CallsPerSnapshotEnforceTheirRange(int value, bool valid) =>
        Assert.AreEqual(valid, (DecisionCaptureLimits.Default with { CallsPerSnapshot = value }).Validate() is null);

    [TestMethod]
    [DataRow(29, false)]
    [DataRow(30, true)]
    [DataRow(730, true)]
    [DataRow(731, false)]
    public void FindingsDaysKeepTheReuseWindowAndAdminDisplaysWorking(int value, bool valid) =>
        Assert.AreEqual(valid, (DecisionCaptureLimits.Default with { FindingsDays = value }).Validate() is null);

    [TestMethod]
    public void ANewInstallationAndAnUpgradedOneStartWithDecisionsOnAndChatOff()
    {
        var fresh = new ModelGatewaySettings(Now);

        Assert.IsTrue(fresh.DecisionCaptureEnabled);
        Assert.IsFalse(fresh.ChatCaptureEnabled);
        Assert.AreEqual(DecisionCaptureLimits.Default, fresh.CaptureLimits);
        var view = DecisionCaptureService.ToView(null);
        Assert.IsTrue(view.DecisionCaptureEnabled, "With no settings row yet, capture still reads as on.");
        Assert.IsFalse(view.ChatCaptureEnabled);
    }

    [TestMethod]
    public void TheSettingsRefuseAnOutOfRangeValueEvenWhenCalledDirectly()
    {
        var settings = new ModelGatewaySettings(Now);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            settings.ConfigureDecisionCapture(true, false, DecisionCaptureLimits.Default with { SnapshotsPerRole = 5_000 }, null, Now));
        Assert.AreEqual(200, settings.CaptureSnapshotsPerRole);
    }

    [TestMethod]
    public async Task EveryOperationIsAdministratorOnlyAndTouchesNothingForAnyoneElse()
    {
        var (service, gateway, store, audit) = Create(admin: false);

        Assert.IsTrue((await service.GetAsync(CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.SaveAsync(new SaveDecisionCaptureSettings(false, true, DecisionCaptureLimits.Default), CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.PreviewAsync(DecisionCaptureLimits.Default, CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.InsightAsync(CancellationToken.None)).Forbidden);
        Assert.IsTrue((await service.PurgeAsync(null, CancellationToken.None)).Forbidden);

        Assert.IsNull(gateway.Settings, "Nothing was created for a non-administrator.");
        Assert.AreEqual(0, store.Purges.Count);
        Assert.AreEqual(0, store.Previews);
        Assert.AreEqual(0, audit.Events.Count);
    }

    [TestMethod]
    public async Task ACraftedOutOfRangeSaveIsRefusedAndChangesAndAuditsNothing()
    {
        var (service, gateway, _, audit) = Create();

        var result = await service.SaveAsync(
            new SaveDecisionCaptureSettings(true, false, DecisionCaptureLimits.Default with { CallsPerSnapshot = 500 }), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Forbidden);
        StringAssert.Contains(result.Error!, "Calls kept per snapshot");
        Assert.IsNull(gateway.Settings);
        Assert.AreEqual(0, audit.Events.Count);
        Assert.IsFalse((await service.PreviewAsync(DecisionCaptureLimits.Default with { FindingsDays = 1 }, CancellationToken.None)).Succeeded);
    }

    [TestMethod]
    public async Task SavingChangesTheSwitchesAndLimitsAndAuditsOldAndNewValuesOnly()
    {
        var (service, gateway, _, audit) = Create();
        var limits = new DecisionCaptureLimits(100, 90, 365, 5, 60);

        var result = await service.SaveAsync(new SaveDecisionCaptureSettings(false, true, limits), CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(gateway.Settings!.DecisionCaptureEnabled);
        Assert.IsTrue(gateway.Settings.ChatCaptureEnabled);
        Assert.AreEqual(limits, gateway.Settings.CaptureLimits);
        Assert.AreEqual(limits, result.Value!.Limits);
        var (action, subject, json) = audit.Events.Single();
        Assert.AreEqual(AuditActions.DecisionCaptureSettingsChanged, action);
        Assert.AreEqual(AuditSubjectTypes.ModelGateway, subject);
        StringAssert.Contains(json, "\"Before\"");
        StringAssert.Contains(json, "\"After\"");
        StringAssert.Contains(json, "\"SnapshotsPerRole\":200", "The old value is recorded.");
        StringAssert.Contains(json, "\"SnapshotsPerRole\":100", "The new value is recorded.");
    }

    [TestMethod]
    public async Task CaptureSettingsAreEditableAndPurgeableWhileTheGatewayItselfIsOff()
    {
        var (service, gateway, store, _) = Create();
        gateway.Settings = new ModelGatewaySettings(Now);
        Assert.IsFalse(gateway.Settings.IsEnabled);

        Assert.IsTrue((await service.SaveAsync(new SaveDecisionCaptureSettings(false, false, DecisionCaptureLimits.Default), CancellationToken.None)).Succeeded);
        Assert.IsTrue((await service.InsightAsync(CancellationToken.None)).Succeeded);
        Assert.IsTrue((await service.PurgeAsync(null, CancellationToken.None)).Succeeded);
        Assert.AreEqual(1, store.Purges.Count);
    }

    [TestMethod]
    public async Task PreviewReportsTheCountOnlyAndChangesNothing()
    {
        var (service, gateway, store, audit) = Create();
        store.Counts = new DecisionCaptureRetentionCounts(4, 7, 2);

        var preview = await service.PreviewAsync(DecisionCaptureLimits.Default with { SnapshotsPerRole = 50 }, CancellationToken.None);

        Assert.IsTrue(preview.Succeeded);
        Assert.AreEqual(13, preview.Value!.Total);
        Assert.IsNull(gateway.Settings);
        Assert.AreEqual(0, audit.Events.Count);
    }

    [TestMethod]
    public async Task PurgeIsScopedToACapturedRoleAndAuditsCountsWithNoContent()
    {
        var (service, _, store, audit) = Create();

        var one = await service.PurgeAsync(ModelRole.CandidateTriage, CancellationToken.None);
        var all = await service.PurgeAsync(null, CancellationToken.None);
        var notCaptured = await service.PurgeAsync(ModelRole.VisionCoverIdentification, CancellationToken.None);

        Assert.IsTrue(one.Succeeded && all.Succeeded);
        Assert.IsFalse(notCaptured.Succeeded, "Cover identification is not a captured role.");
        CollectionAssert.AreEqual(new ModelRole?[] { ModelRole.CandidateTriage, null }, store.Purges);
        Assert.AreEqual(2, audit.Events.Count);
        Assert.IsTrue(audit.Events.All(e => e.Action == AuditActions.DecisionCapturePurged));
        StringAssert.Contains(audit.Events[0].Json, "\"Role\":\"CandidateTriage\"");
        StringAssert.Contains(audit.Events[1].Json, "\"Role\":\"All\"");
        StringAssert.Contains(audit.Events[0].Json, "\"Snapshots\":3");
    }

    [TestMethod]
    public void ExactlySixRolesAreCaptured()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                ModelRole.ArtifactValidationAssessment, ModelRole.ArtifactValidationDecision, ModelRole.CandidateAmbiguityAssessment,
                ModelRole.CandidateTriage, ModelRole.SearchGuidance, ModelRole.IntentInterpretation
            },
            DecisionCaptureRoles.Scored.ToArray());
    }
}

/// <summary>The run identity C1-b will record: which settings, model, reasoning mode and rendered messages produced a reply.</summary>
[TestClass]
public sealed class DecisionCaptureRunIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static (ModelExecutionPolicy Policy, ModelGatewaySettings Settings) Create(ScriptedModel model, ModelReasoningMode reasoning, int? maxOutput = null)
    {
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000", false, null, Now);
        settings.ConfigureRole(ModelRole.CandidateTriage, true, ModelPlacement.Local, "local/alias", null, null, Now, reasoning);
        if (maxOutput is { } cap) settings.ConfigureLimits(ModelExecutionLimits.Default with { MaxOutputTokens = cap }, null, Now);
        settings.SetEnabled(true, null, Now);
        var policy = new ModelExecutionPolicy(
            new ModelRoleResolver(new FakeGatewayStore { Settings = settings }, new PassThroughProtector()),
            model, new FakeLedger(), new ModelExecutionGate(), new ModelCircuitBreaker(), new MutableClock(Now));
        return (policy, settings);
    }

    private static ModelExecutionRequest Request(int? cap = null) =>
        new(ModelRole.CandidateTriage, ModelBudgetClass.BackgroundValidation, null,
            [new ModelChatMessage("system", "Rules."), new ModelChatMessage("user", "Facts.")], JsonObjectOutput: true, MaxOutputTokens: cap);

    [TestMethod]
    public async Task ASuccessfulCallCarriesSettingsReportedModelReasoningModeAndTheExactRenderedHash()
    {
        var model = new ScriptedModel((_, _) => ScriptedModel.Ok("{}", model: "provider/real-model-7b"));
        var (policy, settings) = Create(model, ModelReasoningMode.Low, maxOutput: 700);

        var result = await policy.ExecuteAsync(Request(cap: 300), ModelParse.Ok, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(settings.Id, result.SettingsId);
        Assert.AreEqual("provider/real-model-7b", result.ReportedModel);
        Assert.AreEqual(ModelReasoningMode.Low, result.Reasoning);
        Assert.IsFalse(result.IsCloud);
        // The hash is of the final request, after the caller's tighter cap was applied: 300, not the configured 700.
        var sent = model.Calls.Single().Request;
        Assert.AreEqual(300, sent.MaxOutputTokens);
        Assert.AreEqual(RenderedMessagesIdentity.Compute(sent.Messages, sent.JsonObjectOutput, sent.MaxOutputTokens), result.RenderedMessagesHash);
        StringAssert.StartsWith(result.RenderedMessagesHash!, "rendered-messages/1:");
    }

    [TestMethod]
    public async Task TheClampedCapNotTheRequestedOneIsWhatTheHashIdentifies()
    {
        var model = new ScriptedModel((_, _) => ScriptedModel.Ok("{}"));
        var (policy, _) = Create(model, ModelReasoningMode.Inherit, maxOutput: 256);

        var result = await policy.ExecuteAsync(Request(cap: 900), ModelParse.Ok, CancellationToken.None);

        var sent = model.Calls.Single().Request;
        Assert.AreEqual(256, sent.MaxOutputTokens, "A looser request is clamped to the configured limit.");
        Assert.AreEqual(RenderedMessagesIdentity.Compute(sent.Messages, true, 256), result.RenderedMessagesHash);
    }

    [TestMethod]
    public async Task AControlCharacterLadenReportedModelIsCleanedAndBounded()
    {
        var hostile = "evil\u001b[31m-model\n" + new string('x', 400);
        var model = new ScriptedModel((_, _) => ScriptedModel.Ok("{}", model: hostile));
        var (policy, _) = Create(model, ModelReasoningMode.Inherit);

        var result = await policy.ExecuteAsync(Request(), ModelParse.Ok, CancellationToken.None);

        var reported = result.ReportedModel!;
        Assert.IsFalse(reported.Any(char.IsControl));
        Assert.AreEqual(ModelGatewaySettings.MaxModelNameLength, reported.Length);
    }

    [TestMethod]
    public async Task ARejectedReplyStillCarriesTheIdentityAndARefusalCarriesNone()
    {
        var model = new ScriptedModel((_, _) => ScriptedModel.Ok("not json", model: "provider/m"));
        var (policy, _) = Create(model, ModelReasoningMode.Disabled);

        var rejected = await policy.ExecuteAsync(Request(), _ => ModelParse.SchemaRejected<string>("bad"), CancellationToken.None);
        var refused = await policy.ExecuteAsync(Request() with { Role = ModelRole.SearchGuidance }, ModelParse.Ok, CancellationToken.None);

        Assert.AreEqual(ModelCallOutcome.SchemaRejected, rejected.Outcome);
        Assert.AreEqual("provider/m", rejected.ReportedModel);
        Assert.AreEqual(ModelReasoningMode.Disabled, rejected.Reasoning);
        Assert.IsNotNull(rejected.RenderedMessagesHash);
        Assert.IsTrue(refused.WasRefused);
        Assert.IsNull(refused.ReportedModel);
        Assert.IsNull(refused.RenderedMessagesHash);
        Assert.AreEqual(1, model.Calls.Count, "The refused role never reached the model.");
    }

    [TestMethod]
    public async Task AnImageCallCannotBeHashedAndSimplyHasNoHashRatherThanFailing()
    {
        var model = new ScriptedModel((_, _) => ScriptedModel.Ok("{}"));
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000", false, null, Now);
        settings.ConfigureRole(ModelRole.VisionCoverIdentification, true, ModelPlacement.Local, "local/vision", null, null, Now);
        settings.SetEnabled(true, null, Now);
        var policy = new ModelExecutionPolicy(
            new ModelRoleResolver(new FakeGatewayStore { Settings = settings }, new PassThroughProtector()),
            model, new FakeLedger(), new ModelExecutionGate(), new ModelCircuitBreaker(), new MutableClock(Now));

        var result = await policy.ExecuteAsync(
            new ModelExecutionRequest(ModelRole.VisionCoverIdentification, ModelBudgetClass.InteractiveChat, Guid.NewGuid(),
                [new ModelChatMessage("user", "What is this?", [new ModelImage("image/png", [1, 2, 3])])]),
            ModelParse.Ok, CancellationToken.None);

        Assert.IsTrue(result.Succeeded, "A hashing problem never changes the result of a call.");
        Assert.IsNull(result.RenderedMessagesHash);
    }
}
