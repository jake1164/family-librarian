using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelGateway;

namespace FamilyLibrarian.Infrastructure.Tests.ModelTesting;

/// <summary>
/// M3-5: the manual model capability test, driven by scripted fake models only. No real endpoint, key or model is involved.
/// </summary>
[TestClass]
public sealed class ModelCapabilityTestServiceTests
{
    private static readonly Guid AdminId = Guid.NewGuid();

    private sealed class Rig
    {
        public Rig(Func<ModelConnection, ModelRequest, ModelResponse>? handler = null, bool admin = true, bool signedIn = true)
        {
            Model = new ScriptedModel(handler ?? ScriptedModel.WellBehaved);
            Service = new ModelCapabilityTestService(
                Store, Targets, Model, Gate, Audit, new FakeUser(signedIn ? AdminId : null), new FakeAdmin(admin));
        }

        public FakeSettingsStore Store { get; } = new() { Settings = FakeSettingsStore.Configured() };

        public ScriptedModel Model { get; }

        public FakeTargetSource Targets { get; } = new();

        public ModelTestGate Gate { get; } = new();

        public CapturingAudit Audit { get; } = new();

        public ModelCapabilityTestService Service { get; }

        public Task<ModelTestRunResult> RunAsync(ModelTestTarget target = ModelTestTarget.Primary, bool confirmCloud = false, params ModelCheck[] checks) =>
            Service.RunAsync(new ModelTestRunRequest(target, checks, confirmCloud), CancellationToken.None);
    }

    private static ModelCheckResult Of(ModelTestRunResult result, ModelCheck check) => result.Report!.Results.Single(item => item.Check == check);

    [TestMethod]
    public async Task ADefaultRunWithAWellBehavedModelPassesEveryCheckAndReportsUsageAndLatency()
    {
        var rig = new Rig();

        var result = await rig.RunAsync();

        Assert.AreEqual(ModelTestRunOutcome.Completed, result.Outcome);
        Assert.IsTrue(result.Report!.Results.All(item => item.Status is ModelCheckStatus.Passed), string.Join("; ", result.Report.Results.Select(item => $"{item.Check}={item.Status}: {item.Detail}")));
        Assert.AreEqual(8, result.Report.Results.Count, "The seven original checks plus the separate visible-content check.");
        Assert.AreEqual("local/primary", result.Report.ConfiguredModel);
        CollectionAssert.Contains(result.Report.ReportedModels.ToArray(), "reported-model");
        Assert.AreEqual(rig.Model.Calls.Count, result.Report.TotalCalls);
        Assert.AreEqual((rig.Model.Calls.Count - 1) * 15, result.Report.TotalTokens, "The refused probe reports no usage.");
        Assert.AreEqual((rig.Model.Calls.Count - 1) * 12L + 7, result.Report.TotalLatencyMilliseconds);
        Assert.AreEqual(4, Of(result, ModelCheck.IntentClassification).Calls);
        Assert.AreEqual(60, Of(result, ModelCheck.IntentClassification).TotalTokens);
        Assert.AreEqual(0, Of(result, ModelCheck.MalformedResponses).Calls, "The malformed-response check is offline.");
        Assert.IsFalse(result.Report.IsCloud);
    }

    [TestMethod]
    public async Task EveryCallIsBoundedAndUsesOnlyTheChosenModelOrTheMadeUpProbeAlias()
    {
        var rig = new Rig();

        await rig.RunAsync();

        Assert.IsTrue(rig.Model.Calls.All(call => call.Request.MaxOutputTokens <= 384));
        Assert.IsTrue(rig.Model.Calls.All(call => call.Connection.Timeout == TimeSpan.FromSeconds(30)));
        Assert.IsTrue(rig.Model.Calls.All(call => call.Connection.Model is "local/primary" or "fl-capability-test-no-such-model"),
            "No fallback or other alias is ever used.");
        Assert.IsTrue(rig.Model.Calls.All(call => call.Connection.ApiKey == "secret-key-value"));
        Assert.IsTrue(rig.Model.Calls.All(call => call.Request.Messages.Sum(message => message.Content.Length) < 1_500), "Inputs are small and fixed.");
        Assert.IsLessThanOrEqualTo(12, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task OnlyFictionalTextIsEverSentToTheModel()
    {
        var rig = new Rig();

        await rig.RunAsync();

        var sent = string.Join("\n", rig.Model.Calls.SelectMany(call => call.Request.Messages.Select(message => message.Content)));
        foreach (var fictional in new[] { "Lantern Road", "Mara Quill" })
        {
            StringAssert.Contains(sent, fictional);
        }

        Assert.IsFalse(sent.Contains('@'), "No email addresses.");
        Assert.IsFalse(sent.Contains("http", StringComparison.OrdinalIgnoreCase), "No URLs.");
        Assert.IsFalse(sent.Contains("secret-key-value", StringComparison.Ordinal), "The key is never in a prompt.");
    }

    [TestMethod]
    public async Task ANonAdministratorOrSignedOutCallerCannotRunAndNothingIsCalled()
    {
        var member = new Rig(admin: false);
        var anonymous = new Rig(signedIn: false);

        var memberResult = await member.RunAsync();
        var anonymousResult = await anonymous.RunAsync();

        Assert.AreEqual(ModelTestRunOutcome.Forbidden, memberResult.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Forbidden, anonymousResult.Outcome);
        Assert.IsEmpty(member.Model.Calls);
        Assert.IsEmpty(anonymous.Model.Calls);
        Assert.IsEmpty(member.Audit.Events);
    }

    [TestMethod]
    public async Task ADisabledOrUnconfiguredFeatureMakesNoCall()
    {
        var disabled = new Rig();
        disabled.Store.Settings = FakeSettingsStore.Configured(enabled: false);
        var missing = new Rig();
        missing.Store.Settings = null;
        var noComparison = new Rig();
        noComparison.Store.Settings = FakeSettingsStore.Configured();
        noComparison.Store.Settings.Configure("local/primary", null, null, false, 30, 384, null, DateTimeOffset.UnixEpoch);

        Assert.AreEqual(ModelTestRunOutcome.Disabled, (await disabled.RunAsync()).Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Disabled, (await missing.RunAsync()).Outcome);
        Assert.AreEqual(ModelTestRunOutcome.NotConfigured, (await noComparison.RunAsync(ModelTestTarget.Comparison)).Outcome, "No comparison alias is set.");
        Assert.IsEmpty(disabled.Model.Calls);
        Assert.IsEmpty(missing.Model.Calls);
        Assert.IsEmpty(noComparison.Model.Calls);
    }

    [TestMethod]
    public async Task ACloudRunNeedsBothTheCloudSettingAndAnExplicitConfirmationAndNeverFallsBackToCloud()
    {
        var off = new Rig();
        off.Store.Settings = FakeSettingsStore.Configured(allowCloud: false, cloud: "cloud/model");
        var allowed = new Rig();
        allowed.Store.Settings = FakeSettingsStore.Configured(allowCloud: true, cloud: "cloud/model");

        var settingOff = await off.RunAsync(ModelTestTarget.Cloud, confirmCloud: true);
        var notConfirmed = await allowed.RunAsync(ModelTestTarget.Cloud, confirmCloud: false);
        var primary = await allowed.RunAsync(ModelTestTarget.Primary);
        var confirmed = await allowed.RunAsync(ModelTestTarget.Cloud, confirmCloud: true, ModelCheck.Connectivity);

        Assert.AreEqual(ModelTestRunOutcome.CloudNotPermitted, settingOff.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.CloudNotPermitted, notConfirmed.Outcome);
        Assert.IsEmpty(off.Model.Calls);
        Assert.IsTrue(allowed.Model.Calls.Take(allowed.Model.Calls.Count - 1).All(call => call.Connection.Model != "cloud/model"),
            "A primary run never touches the cloud model.");
        Assert.AreEqual(ModelTestRunOutcome.Completed, primary.Outcome);
        Assert.IsTrue(confirmed.Report!.IsCloud);
        Assert.AreEqual("cloud/model", allowed.Model.Calls[^1].Connection.Model);
    }

    [TestMethod]
    public async Task TheMadeUpAliasProbeIsNeverSentToACloudModel()
    {
        var rig = new Rig();
        rig.Store.Settings = FakeSettingsStore.Configured(allowCloud: true, cloud: "cloud/model");

        var result = await rig.RunAsync(ModelTestTarget.Cloud, confirmCloud: true, ModelCheck.ModelUnavailable);

        Assert.AreEqual(ModelCheckStatus.Skipped, Of(result, ModelCheck.ModelUnavailable).Status);
        Assert.IsEmpty(rig.Model.Calls);
    }

    [TestMethod]
    public async Task OnlyOneRunPerAdministratorAtATimeAndTheLockIsReleasedAfterwards()
    {
        var rig = new Rig();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Model.BeforeAnswer = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var first = rig.RunAsync(checks: ModelCheck.Connectivity);
        await entered.Task;
        var second = await rig.RunAsync(checks: ModelCheck.Connectivity);
        release.SetResult();
        var firstResult = await first;
        rig.Model.BeforeAnswer = null;
        var third = await rig.RunAsync(checks: ModelCheck.Connectivity);

        Assert.AreEqual(ModelTestRunOutcome.Busy, second.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Completed, firstResult.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Completed, third.Outcome);
        Assert.AreEqual(2, rig.Model.Calls.Count, "The refused run made no call.");
    }

    [TestMethod]
    public async Task AnUnreachableEndpointEndsTheRunAfterOneCallWithNoRetry()
    {
        var rig = new Rig((_, _) => ScriptedModel.Fail(ModelFailureKind.Unreachable, "connection refused"));

        var result = await rig.RunAsync();

        Assert.AreEqual(1, rig.Model.Calls.Count, "No retry and no further checks against a dead endpoint.");
        Assert.AreEqual(ModelCheckStatus.Error, Of(result, ModelCheck.Connectivity).Status);
        StringAssert.Contains(Of(result, ModelCheck.Connectivity).Detail, "could not be reached");
        Assert.AreEqual(ModelCheckStatus.Skipped, Of(result, ModelCheck.IntentClassification).Status);
        Assert.AreEqual(ModelCheckStatus.Skipped, Of(result, ModelCheck.ToolCapability).Status);
        Assert.AreEqual(ModelCheckStatus.Passed, Of(result, ModelCheck.MalformedResponses).Status, "The offline check still runs.");
    }

    [TestMethod]
    [DataRow(ModelFailureKind.Timeout, "did not answer within the timeout")]
    [DataRow(ModelFailureKind.Unauthorized, "refused the API key")]
    [DataRow(ModelFailureKind.ModelUnavailable, "does not know that model alias")]
    public async Task EachFailureKindIsReportedInPlainWordsAndStopsTheRun(ModelFailureKind kind, string expected)
    {
        var rig = new Rig((_, _) => ScriptedModel.Fail(kind));

        var result = await rig.RunAsync(checks: [ModelCheck.Connectivity, ModelCheck.StructuredJson]);

        StringAssert.Contains(Of(result, ModelCheck.Connectivity).Detail, expected);
        Assert.AreEqual(ModelCheckStatus.Skipped, Of(result, ModelCheck.StructuredJson).Status);
        Assert.AreEqual(1, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ARateLimitedOrServerErrorReplyIsReportedAndDoesNotStopLaterChecks()
    {
        var rig = new Rig((connection, request) => request.JsonObjectOutput
            ? ScriptedModel.Fail(ModelFailureKind.RateLimited, "HTTP 429")
            : ScriptedModel.WellBehaved(connection, request));

        var result = await rig.RunAsync(checks: [ModelCheck.StructuredJson, ModelCheck.Connectivity]);

        Assert.AreEqual(ModelCheckStatus.Error, Of(result, ModelCheck.StructuredJson).Status);
        Assert.AreEqual(ModelCheckStatus.Passed, Of(result, ModelCheck.Connectivity).Status);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("```json\n{\"title\":\"A\",\"author\":\"B\",\"year\":2019}\n```")]
    [DataRow("{\"title\":\"A\",\"author\":\"B\",\"year\":2019,\"confidence\":0.97}")]
    [DataRow("{\"title\":\"A\",\"author\":\"B\"}")]
    [DataRow("")]
    public async Task AnInvalidStructuredReplyIsRejectedNotInterpreted(string reply)
    {
        var rig = new Rig((_, _) => ScriptedModel.Ok(reply));

        var result = await rig.RunAsync(checks: ModelCheck.StructuredJson);

        Assert.AreEqual(ModelCheckStatus.Failed, Of(result, ModelCheck.StructuredJson).Status);
        StringAssert.Contains(Of(result, ModelCheck.StructuredJson).Detail, "rejected");
    }

    [TestMethod]
    public async Task AMatchAdvisoryThatCitesUnsuppliedEvidenceOrAddsAConfidenceNumberIsRejected()
    {
        var invented = new Rig((_, _) => ScriptedModel.Ok("{\"assessment\":\"likely_match\",\"reasons\":[{\"text\":\"Looks right.\",\"evidence_id\":\"ev-99\"}]}"));
        var confident = new Rig((_, _) => ScriptedModel.Ok("{\"assessment\":\"likely_match\",\"confidence\":0.97,\"reasons\":[{\"text\":\"Looks right.\",\"evidence_id\":\"ev-1\"}]}"));

        var inventedResult = await invented.RunAsync(checks: ModelCheck.MatchAdvisory);
        var confidentResult = await confident.RunAsync(checks: ModelCheck.MatchAdvisory);

        Assert.AreEqual(ModelCheckStatus.Failed, Of(inventedResult, ModelCheck.MatchAdvisory).Status);
        StringAssert.Contains(Of(inventedResult, ModelCheck.MatchAdvisory).Detail, "evidence id");
        Assert.AreEqual(ModelCheckStatus.Failed, Of(confidentResult, ModelCheck.MatchAdvisory).Status, "A self-reported confidence is not accepted as data.");
    }

    [TestMethod]
    public async Task AValidButWrongAdvisoryPassesTheShapeCheckAndIsReportedAsDisagreeingNotTrusted()
    {
        var rig = new Rig((_, _) => ScriptedModel.Ok("{\"assessment\":\"likely_match\",\"reasons\":[{\"text\":\"Same words.\",\"evidence_id\":\"ev-1\"}]}"));

        var result = await rig.RunAsync(checks: ModelCheck.MatchAdvisory);

        var check = Of(result, ModelCheck.MatchAdvisory);
        Assert.AreEqual(ModelCheckStatus.Passed, check.Status);
        StringAssert.Contains(check.Detail, "1 of 2 agreed");
        StringAssert.Contains(check.Detail, "no decision is made from it");
    }

    [TestMethod]
    public async Task ToolSupportIsReportedAsObservedNeverAssumedAndNeverExecuted()
    {
        var calls = new Rig((_, _) => ScriptedModel.Ok(null, tools: [new ModelToolCall("lookup_catalog_title")]));
        var textOnly = new Rig((_, _) => ScriptedModel.Ok("I would look it up."));
        var rejects = new Rig((_, _) => ScriptedModel.Fail(ModelFailureKind.BadRequest, "HTTP 400: tools are not supported"));
        var invents = new Rig((_, _) => ScriptedModel.Ok(null, tools: [new ModelToolCall("delete_everything")]));

        Assert.AreEqual(ModelCheckStatus.Passed, Of(await calls.RunAsync(checks: ModelCheck.ToolCapability), ModelCheck.ToolCapability).Status);
        Assert.AreEqual(ModelCheckStatus.NotSupported, Of(await textOnly.RunAsync(checks: ModelCheck.ToolCapability), ModelCheck.ToolCapability).Status);
        Assert.AreEqual(ModelCheckStatus.NotSupported, Of(await rejects.RunAsync(checks: ModelCheck.ToolCapability), ModelCheck.ToolCapability).Status);
        var inventedResult = Of(await invents.RunAsync(checks: ModelCheck.ToolCapability), ModelCheck.ToolCapability);
        Assert.AreEqual(ModelCheckStatus.Failed, inventedResult.Status);
        StringAssert.Contains(inventedResult.Detail, "never declared");
        Assert.AreEqual(1, invents.Model.Calls.Count, "Asking for a tool leads to no further call.");
    }

    [TestMethod]
    public async Task AModelThatAnswersAMadeUpAliasIsFlaggedAndAProperRefusalPasses()
    {
        var honest = new Rig();
        var substitutes = new Rig((_, _) => ScriptedModel.Ok("pong"));

        var honestResult = await honest.RunAsync(checks: ModelCheck.ModelUnavailable);
        var substitutesResult = await substitutes.RunAsync(checks: ModelCheck.ModelUnavailable);

        Assert.AreEqual(ModelCheckStatus.Passed, Of(honestResult, ModelCheck.ModelUnavailable).Status);
        Assert.AreEqual(ModelCheckStatus.Failed, Of(substitutesResult, ModelCheck.ModelUnavailable).Status);
        Assert.AreEqual("fl-capability-test-no-such-model", honest.Model.Calls.Single().Connection.Model);
        Assert.AreEqual(16, honest.Model.Calls.Single().Request.MaxOutputTokens);
    }

    [TestMethod]
    public async Task TheMalformedResponseSelfTestMakesNoModelCallAndPasses()
    {
        var rig = new Rig();

        var result = await rig.RunAsync(checks: ModelCheck.MalformedResponses);

        Assert.AreEqual(ModelCheckStatus.Passed, Of(result, ModelCheck.MalformedResponses).Status);
        Assert.IsEmpty(rig.Model.Calls);
    }

    [TestMethod]
    public async Task SamplesAreShortPlainTextAndTheAuditRecordNeverContainsPromptsOrReplies()
    {
        var hostile = "pong" + new string('x', 2_000) + "\u0007<script>alert(1)</script>";
        var rig = new Rig((_, _) => ScriptedModel.Ok(hostile));

        var result = await rig.RunAsync(checks: ModelCheck.AssistantContent);

        var sample = Of(result, ModelCheck.AssistantContent).Sample!;
        Assert.IsLessThanOrEqualTo(301, sample.Length);
        Assert.IsFalse(sample.Any(char.IsControl));
        var audit = rig.Audit.Events.Single();
        Assert.AreEqual("model_test.run", audit.Action);
        Assert.IsFalse(audit.Json.Contains("pong", StringComparison.Ordinal), "No model text in the audit record.");
        Assert.IsFalse(audit.Json.Contains("connectivity probe", StringComparison.Ordinal), "No prompt text in the audit record.");
        Assert.IsFalse(audit.Json.Contains("secret-key-value", StringComparison.Ordinal));
        StringAssert.Contains(audit.Json, "local/primary");
        StringAssert.Contains(audit.Json, "TotalCalls");
    }

    [TestMethod]
    public async Task UnknownChecksOrTargetsAreRefusedBeforeAnyCall()
    {
        var rig = new Rig();

        var result = await rig.Service.RunAsync(new ModelTestRunRequest((ModelTestTarget)99, null, false), CancellationToken.None);
        var badCheck = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Primary, [(ModelCheck)99], false), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Invalid, result.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Invalid, badCheck.Outcome);
        Assert.IsEmpty(rig.Model.Calls);
    }

    [TestMethod]
    public async Task AKeyThatCannotBeDecryptedStopsTheRunBeforeAnyCall()
    {
        var rig = new Rig();
        rig.Targets.Connection = new ModelTestConnectionInfo(null, null, false, "The stored API key could not be decrypted. Enter it again.");

        var result = await rig.RunAsync();

        Assert.AreEqual(ModelTestRunOutcome.NotConfigured, result.Outcome);
        Assert.IsEmpty(rig.Model.Calls);
    }

    [TestMethod]
    public async Task CancellationByTheCallerPropagatesInsteadOfBeingReportedAsAResult()
    {
        var rig = new Rig();
        using var cancellation = new CancellationTokenSource();
        rig.Model.BeforeAnswer = async () =>
        {
            await cancellation.CancelAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            await Task.CompletedTask;
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Primary, [ModelCheck.Connectivity], false), cancellation.Token));
        Assert.IsNotNull(rig.Gate.TryEnter(AdminId), "The per-administrator lock was released.");
    }


    private static ModelResponse Reply(string? content, string? finish, ModelUsage? usage = null) =>
        new(true, ModelFailureKind.None, null, content, [], finish, "reported-model", usage ?? new ModelUsage(10, 5, 15), 12);

    [TestMethod]
    public async Task ASuccessfulReplyWithNoVisibleTextIsAConnectionNotANetworkFailure()
    {
        var rig = new Rig((_, _) => Reply(string.Empty, "stop"));

        var result = await rig.RunAsync(checks: [ModelCheck.Connectivity, ModelCheck.AssistantContent]);

        Assert.AreEqual(ModelCheckStatus.Passed, Of(result, ModelCheck.Connectivity).Status, "The transport worked.");
        StringAssert.StartsWith(Of(result, ModelCheck.Connectivity).Detail, "Connected");
        var content = Of(result, ModelCheck.AssistantContent);
        Assert.AreEqual(ModelCheckStatus.Failed, content.Status, "No usable text is reported separately, as a failure, not as an error.");
        Assert.IsFalse(content.Detail.Contains("reached", StringComparison.OrdinalIgnoreCase) && content.Detail.Contains("endpoint could not", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(content.OutputLimitReached, "Without a length finish reason truncation is not claimed.");
    }

    [TestMethod]
    public async Task AReplyCutOffAtTheOutputLimitIsReportedAsExhaustionAndNeverAccepted()
    {
        // A truncated JSON reply that would even parse as a prefix of the right answer.
        var rig = new Rig((_, request) => Reply(request.JsonObjectOutput ? "{\"title\":\"The Lantern Road\"," : string.Empty, "length"));

        var result = await rig.RunAsync(checks: [ModelCheck.AssistantContent, ModelCheck.StructuredJson, ModelCheck.IntentClassification]);

        Assert.IsTrue(Of(result, ModelCheck.AssistantContent).OutputLimitReached);
        Assert.AreEqual(ModelCheckStatus.Failed, Of(result, ModelCheck.AssistantContent).Status);
        Assert.AreEqual(ModelCheckStatus.Failed, Of(result, ModelCheck.StructuredJson).Status);
        StringAssert.Contains(Of(result, ModelCheck.StructuredJson).Detail, "not accepted");
        Assert.AreEqual(ModelCheckStatus.Failed, Of(result, ModelCheck.IntentClassification).Status);
        Assert.IsTrue(Of(result, ModelCheck.IntentClassification).OutputLimitReached);
    }

    [TestMethod]
    public async Task TheConnectivityProbeIsTinyButTheContentCheckUsesTheConfiguredLimit()
    {
        var rig = new Rig();

        await rig.RunAsync(checks: [ModelCheck.Connectivity, ModelCheck.AssistantContent]);

        Assert.AreEqual(16, rig.Model.Calls[0].Request.MaxOutputTokens);
        Assert.AreEqual(384, rig.Model.Calls[1].Request.MaxOutputTokens, "No hidden 32-token clamp: the configured limit is used.");
    }

    [TestMethod]
    public async Task ThinkingEnabledIsTheDefaultAndSendsNoReasoningSetting()
    {
        var rig = new Rig();

        await rig.RunAsync();

        Assert.IsTrue(rig.Model.Calls.All(call => call.Request.Reasoning == ModelReasoningMode.Inherit));
    }

    [TestMethod]
    public async Task AReasoningOverrideAppliesToThatRunOnlyAndChangesNoStoredSetting()
    {
        var rig = new Rig();

        var result = await rig.Service.RunAsync(
            new ModelTestRunRequest(ModelTestTarget.Primary, [ModelCheck.AssistantContent], false, ModelReasoningMode.Disabled, 20, 777), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Completed, result.Outcome);
        Assert.AreEqual(ModelReasoningMode.Disabled, rig.Model.Calls.Single().Request.Reasoning);
        Assert.AreEqual(777, rig.Model.Calls.Single().Request.MaxOutputTokens);
        Assert.AreEqual(TimeSpan.FromSeconds(20), rig.Model.Calls.Single().Connection.Timeout);
        Assert.AreEqual(ModelReasoningMode.Disabled, result.Report!.Reasoning);
        Assert.AreEqual(ModelReasoningMode.Inherit, rig.Store.Settings!.Reasoning, "A run never edits the saved test settings.");
        Assert.AreEqual(0, rig.Store.Saves);
        StringAssert.Contains(rig.Audit.Events.Single().Json, "Disabled");
    }

    [TestMethod]
    public async Task AnOutOfRangeRunOverrideIsRefusedBeforeAnyCall()
    {
        var rig = new Rig();

        var result = await rig.Service.RunAsync(
            new ModelTestRunRequest(ModelTestTarget.Primary, null, false, null, null, 999_999), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Invalid, result.Outcome);
        Assert.AreEqual(0, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ARejectedReasoningSettingIsReportedAndNeverRetriedWithoutIt()
    {
        var rig = new Rig((connection, request) => request.Reasoning == ModelReasoningMode.Disabled
            ? ScriptedModel.Fail(ModelFailureKind.UnsupportedCapability, "HTTP 400: reasoning_effort is not supported")
            : ScriptedModel.WellBehaved(connection, request));

        var result = await rig.Service.RunAsync(
            new ModelTestRunRequest(ModelTestTarget.Primary, [ModelCheck.AssistantContent], false, ModelReasoningMode.Disabled), CancellationToken.None);

        var item = Of(result, ModelCheck.AssistantContent);
        Assert.AreEqual(ModelCheckStatus.Error, item.Status);
        StringAssert.Contains(item.Detail, "reasoning");
        Assert.AreEqual(1, rig.Model.Calls.Count, "Exactly one call: no silent retry with different parameters.");
    }

    [TestMethod]
    public async Task ARoleTargetTestsExactlyThatRolesConfiguredModelAndTouchesNothingElse()
    {
        var rig = new Rig();
        rig.Targets.Roles[ModelRole.IntentInterpretation] = new ModelTestRoleTarget(ModelRole.IntentInterpretation, "role/local-alias", false);

        var result = await rig.Service.RunAsync(
            new ModelTestRunRequest(ModelTestTarget.Role, [ModelCheck.AssistantContent], false, Role: ModelRole.IntentInterpretation), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Completed, result.Outcome);
        Assert.AreEqual("role/local-alias", result.Report!.ConfiguredModel);
        Assert.IsFalse(result.Report.IsCloud);
        Assert.AreEqual("role/local-alias", rig.Model.Calls.Single().Connection.Model);
    }

    [TestMethod]
    public async Task ACloudPlacedRoleNeedsTheTestCloudSwitchAndAConfirmationWhateverProductionAllows()
    {
        var rig = new Rig();
        rig.Targets.Roles[ModelRole.ConversationResponse] = new ModelTestRoleTarget(ModelRole.ConversationResponse, "role/cloud-alias", true);

        var off = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Role, [ModelCheck.Connectivity], true, Role: ModelRole.ConversationResponse), CancellationToken.None);
        rig.Store.Settings = FakeSettingsStore.Configured(allowCloud: true, cloud: "cloud/test");
        var unconfirmed = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Role, [ModelCheck.Connectivity], false, Role: ModelRole.ConversationResponse), CancellationToken.None);
        var confirmed = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Role, [ModelCheck.Connectivity], true, Role: ModelRole.ConversationResponse), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.CloudNotPermitted, off.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.CloudNotPermitted, unconfirmed.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Completed, confirmed.Outcome);
        Assert.IsTrue(confirmed.Report!.IsCloud);
        Assert.AreEqual(1, rig.Model.Calls.Count, "Only the confirmed run made a call.");
    }

    [TestMethod]
    public async Task ARoleWithNoModelAndAnAliasWithoutADeclaredPlacementAreRefusedBeforeAnyCall()
    {
        var rig = new Rig();

        var noRole = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Role, null, false, Role: ModelRole.VisionCoverIdentification), CancellationToken.None);
        var noRoleChosen = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Role, null, false), CancellationToken.None);
        var undeclared = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Alias, null, false, Alias: "desktop/looks-local"), CancellationToken.None);
        var unsafeAlias = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Alias, null, false, Alias: "bad alias;", AliasIsCloud: false), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.NotConfigured, noRole.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Invalid, noRoleChosen.Outcome);
        Assert.AreEqual(ModelTestRunOutcome.Invalid, undeclared.Outcome, "A name is never taken as proof that a model is local.");
        Assert.AreEqual(ModelTestRunOutcome.Invalid, unsafeAlias.Outcome);
        Assert.IsEmpty(rig.Model.Calls);
    }

    [TestMethod]
    public async Task ATestOnlyAliasRunsWithItsDeclaredPlacementAndACloudOneIsGatedLikeAnyOther()
    {
        var rig = new Rig();

        var local = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Alias, [ModelCheck.Connectivity], false, Alias: "scratch/model", AliasIsCloud: false), CancellationToken.None);
        var cloudBlocked = await rig.Service.RunAsync(new ModelTestRunRequest(ModelTestTarget.Alias, [ModelCheck.Connectivity], true, Alias: "scratch/model", AliasIsCloud: true), CancellationToken.None);

        Assert.AreEqual(ModelTestRunOutcome.Completed, local.Outcome);
        Assert.AreEqual("scratch/model", rig.Model.Calls.Single().Connection.Model);
        Assert.AreEqual(ModelTestRunOutcome.CloudNotPermitted, cloudBlocked.Outcome, "Declared cloud, cloud testing is off.");
    }

    [TestMethod]
    public async Task ARunNeverWritesTestSettingsAndMakesNoCallWhenTheSharedConnectionIsMissing()
    {
        var rig = new Rig();
        rig.Targets.Connection = new ModelTestConnectionInfo(null, null, false, "Set the endpoint on the Models & roles tab first.");

        var result = await rig.RunAsync();

        Assert.AreEqual(ModelTestRunOutcome.NotConfigured, result.Outcome);
        StringAssert.Contains(result.Error!, "Models & roles");
        Assert.IsEmpty(rig.Model.Calls);
        Assert.AreEqual(0, rig.Store.Saves);
    }
}
