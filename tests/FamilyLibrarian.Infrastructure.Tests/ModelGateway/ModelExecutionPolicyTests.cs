using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.ModelTesting;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.ModelGateway;

/// <summary>An in-memory ledger with the same counting rules as the real one (pending reservations count).</summary>
internal sealed class FakeLedger : IModelUsageLedger
{
    public List<ModelUsageRecord> Records { get; } = [];

    public bool FailAdds { get; set; }

    public Task<ModelUsageTotals> GetTotalsAsync(ModelBudgetClass budget, Guid? userId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var all = Records.Where(record => record.OccurredAtUtc >= since).ToList();
        var installation = all.Where(record => record.BudgetClass == budget).ToList();
        var user = installation.Where(record => userId is not null && record.UserId == userId).ToList();
        var cloud = all.Where(record => record.IsCloud).ToList();
        return Task.FromResult(new ModelUsageTotals(
            user.Count, user.Sum(record => (long)record.ChargedTokens), installation.Count, installation.Sum(record => (long)record.ChargedTokens),
            cloud.Count, cloud.Sum(record => (long)record.ChargedTokens)));
    }

    public Task AddAsync(ModelUsageRecord record, CancellationToken cancellationToken)
    {
        if (FailAdds) throw new InvalidOperationException("database down");
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(Guid recordId, ModelCallOutcome outcome, long latencyMilliseconds, ModelUsage? usage, CancellationToken cancellationToken)
    {
        Records.Single(record => record.Id == recordId).Complete(outcome, latencyMilliseconds, usage?.PromptTokens, usage?.CompletionTokens, usage?.TotalTokens);
        return Task.CompletedTask;
    }

    public Task<ModelUsageSummary> SummarizeAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(new ModelUsageSummary(since, []));
}

internal sealed class MutableClock(DateTimeOffset now) : FamilyLibrarian.Application.Abstractions.IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>M4-1: bounded, accounted, classified model execution that can fail without affecting anything else.</summary>
[TestClass]
public sealed class ModelExecutionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    private sealed class Rig
    {
        public Rig(ScriptedModel model, ModelGatewaySettings? settings = null, FakeLedger? ledger = null)
        {
            Model = model;
            Settings = settings ?? Configured();
            Ledger = ledger ?? new FakeLedger();
            Clock = new MutableClock(Now);
            Gate = new ModelExecutionGate();
            Breaker = new ModelCircuitBreaker();
            Policy = new ModelExecutionPolicy(
                new ModelRoleResolver(new FakeGatewayStore { Settings = Settings }, new PassThroughProtector()),
                model, Ledger, Gate, Breaker, Clock);
        }

        public ScriptedModel Model { get; }

        public ModelGatewaySettings Settings { get; }

        public FakeLedger Ledger { get; }

        public MutableClock Clock { get; }

        public ModelExecutionGate Gate { get; }

        public ModelCircuitBreaker Breaker { get; }

        public ModelExecutionPolicy Policy { get; }
    }

    private static ModelGatewaySettings Configured(
        ModelExecutionLimits? limits = null, bool cloud = false, ModelRole role = ModelRole.CandidateAmbiguityAssessment)
    {
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000", cloud, null, Now);
        settings.ConfigureRole(role, true, cloud ? ModelPlacement.Cloud : ModelPlacement.Local, "local/alias", "cloud/alias", null, Now);
        settings.ConfigureRole(ModelRole.ArtifactValidationAssessment, true, ModelPlacement.Local, "local/alias", null, null, Now);
        if (limits is not null) settings.ConfigureLimits(limits, null, Now);
        settings.SetEnabled(true, null, Now);
        return settings;
    }

    private static ModelExecutionRequest Chat(Guid? user = null, string text = "Is this the same book?", ModelRole role = ModelRole.CandidateAmbiguityAssessment) =>
        new(role, ModelBudgetClass.InteractiveChat, user ?? Alice, [new ModelChatMessage("user", text)], JsonObjectOutput: true);

    private static ModelExecutionRequest Background(string text = "Check this file.") =>
        new(ModelRole.ArtifactValidationAssessment, ModelBudgetClass.BackgroundValidation, null, [new ModelChatMessage("user", text)], JsonObjectOutput: true);

    private static ModelParseResult<string> AcceptAll(string text) => ModelParse.Ok(text);

    private static Task<ModelExecution<string>> RunAsync(Rig rig, ModelExecutionRequest request) =>
        rig.Policy.ExecuteAsync(request, AcceptAll, CancellationToken.None);

    [TestMethod]
    public async Task ASuccessfulCallIsRecordedWithTheProvidersOwnUsage()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{\"a\":1}", new ModelUsage(100, 20, 120))));

        var result = await RunAsync(rig, Chat());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("{\"a\":1}", result.Value);
        var record = rig.Ledger.Records.Single();
        Assert.AreEqual(ModelCallOutcome.Succeeded, record.Outcome);
        Assert.AreEqual(120, record.ReportedTotalTokens);
        Assert.AreEqual(120, record.ChargedTokens);
        Assert.IsFalse(record.UsageEstimated);
        Assert.AreEqual("local/alias", rig.Model.Calls.Single().Connection.Model);
    }

    [TestMethod]
    public async Task MissingUsageIsNeverReportedAsZeroAndIsChargedAConservativeEstimate()
    {
        var rig = new Rig(new ScriptedModel((_, _) => new ModelResponse(true, ModelFailureKind.None, null, "{}", [], "stop", "m", null, 5)));

        var result = await RunAsync(rig, Chat());

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Usage, "The result must say usage is unknown, not zero.");
        var record = rig.Ledger.Records.Single();
        Assert.IsNull(record.ReportedTotalTokens);
        Assert.IsTrue(record.UsageEstimated);
        Assert.IsTrue(record.ChargedTokens >= ModelExecutionLimits.Default.MaxOutputTokens, "The budget is charged at least the output cap.");
    }

    [TestMethod]
    public async Task EachFailureKindIsClassifiedDistinctly()
    {
        async Task<ModelCallOutcome> OutcomeOf(Func<ModelConnection, ModelRequest, ModelResponse> handler, Func<string, ModelParseResult<string>>? parse = null)
        {
            var rig = new Rig(new ScriptedModel(handler));
            return (await rig.Policy.ExecuteAsync(Chat(), parse ?? AcceptAll, CancellationToken.None)).Outcome;
        }

        Assert.AreEqual(ModelCallOutcome.TransportFailure, await OutcomeOf((_, _) => ScriptedModel.Fail(ModelFailureKind.Unreachable)));
        Assert.AreEqual(ModelCallOutcome.TransportFailure, await OutcomeOf((_, _) => ScriptedModel.Fail(ModelFailureKind.ServerError)));
        Assert.AreEqual(ModelCallOutcome.Timeout, await OutcomeOf((_, _) => ScriptedModel.Fail(ModelFailureKind.Timeout)));
        Assert.AreEqual(ModelCallOutcome.ModelUnavailable, await OutcomeOf((_, _) => ScriptedModel.Fail(ModelFailureKind.ModelUnavailable)));
        Assert.AreEqual(ModelCallOutcome.UnsupportedCapability, await OutcomeOf((_, _) => ScriptedModel.Fail(ModelFailureKind.UnsupportedCapability)));
        Assert.AreEqual(ModelCallOutcome.EmptyContent, await OutcomeOf((_, _) => ScriptedModel.Ok("")));
        Assert.AreEqual(ModelCallOutcome.EmptyContent, await OutcomeOf((_, _) => ScriptedModel.Ok(null)));
        Assert.AreEqual(ModelCallOutcome.EmptyContent, await OutcomeOf((_, _) => ScriptedModel.Ok("   \n")));
        Assert.AreEqual(ModelCallOutcome.InvalidJson, await OutcomeOf((_, _) => ScriptedModel.Ok("nope"), _ => ModelParse.NotJson<string>("not json")));
        Assert.AreEqual(ModelCallOutcome.SchemaRejected, await OutcomeOf((_, _) => ScriptedModel.Ok("{}"), _ => ModelParse.SchemaRejected<string>("extra field")));
        Assert.AreEqual(ModelCallOutcome.SchemaRejected, await OutcomeOf((_, _) => ScriptedModel.Ok("{}"), _ => throw new FormatException("boom")));
        Assert.AreEqual(ModelCallOutcome.OutputTooLarge, await OutcomeOf((_, _) => ScriptedModel.Ok(new string('x', 512 * 8 + 1))));
    }

    [TestMethod]
    public async Task ThereAreNoRetriesAndNoFallbackToAnotherModel()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Fail(ModelFailureKind.Unreachable)));

        var result = await RunAsync(rig, Chat());

        Assert.AreEqual(ModelCallOutcome.TransportFailure, result.Outcome);
        Assert.AreEqual(1, rig.Model.Calls.Count, "A failed call is not retried.");
        Assert.AreEqual(1, rig.Ledger.Records.Count);
    }

    [TestMethod]
    public async Task ACloudRoleWithoutCloudPermissionOrAllowanceMakesNoCallAtAll()
    {
        // Placement is cloud but cloud use is not allowed: unavailable, and the local alias is never tried instead.
        var notAllowed = new ModelGatewaySettings(Now);
        notAllowed.Configure("http://litellm.example.test:4000", false, null, Now);
        notAllowed.ConfigureRole(ModelRole.CandidateAmbiguityAssessment, true, ModelPlacement.Cloud, "local/alias", "cloud/alias", null, Now);
        notAllowed.SetEnabled(true, null, Now);
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")), notAllowed);

        var refused = await RunAsync(rig, Chat());

        Assert.AreEqual(ModelCallOutcome.RoleUnavailable, refused.Outcome);
        Assert.AreEqual(0, rig.Model.Calls.Count);

        // Cloud allowed, but the cloud allowance is still the default zero: also no call.
        var zeroAllowance = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")), Configured(cloud: true));
        var stopped = await RunAsync(zeroAllowance, Chat());
        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, stopped.Outcome);
        Assert.AreEqual(0, zeroAllowance.Model.Calls.Count);

        // With an explicit allowance the call goes to the cloud alias.
        var granted = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")),
            Configured(ModelExecutionLimits.Default with { CloudDailyRequests = 5, CloudDailyTokens = 50_000 }, cloud: true));
        var allowed = await RunAsync(granted, Chat());
        Assert.IsTrue(allowed.Succeeded);
        Assert.IsTrue(allowed.IsCloud);
        Assert.AreEqual("cloud/alias", granted.Model.Calls.Single().Connection.Model);
    }

    [TestMethod]
    public async Task APersonsDailyRequestAllowanceIsAHardStopThatDoesNotAffectOthers()
    {
        var limits = ModelExecutionLimits.Default with { Interactive = ModelExecutionLimits.Default.Interactive with { UserDailyRequests = 2 } };
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}", new ModelUsage(5, 5, 10))), Configured(limits));

        Assert.IsTrue((await RunAsync(rig, Chat(Alice))).Succeeded);
        Assert.IsTrue((await RunAsync(rig, Chat(Alice))).Succeeded);
        var third = await RunAsync(rig, Chat(Alice));
        var other = await RunAsync(rig, Chat(Bob));

        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, third.Outcome);
        Assert.IsTrue(third.WasRefused);
        Assert.AreEqual(2, rig.Model.Calls.Count - 1, "Alice's third call was never sent; Bob's was.");
        Assert.IsTrue(other.Succeeded);
    }

    [TestMethod]
    public async Task TokenBudgetsStopCallsAndTheInstallationLimitCoversEveryone()
    {
        var tight = ModelExecutionLimits.Default with
        {
            Interactive = ModelExecutionLimits.Default.Interactive with { InstallationDailyRequests = 1 }
        };
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}", new ModelUsage(5, 5, 10))), Configured(tight));

        Assert.IsTrue((await RunAsync(rig, Chat(Alice))).Succeeded);
        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, (await RunAsync(rig, Chat(Bob))).Outcome);

        var tokens = ModelExecutionLimits.Default with
        {
            Interactive = ModelExecutionLimits.Default.Interactive with { UserDailyTokens = 50 }
        };
        var tokenRig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")), Configured(tokens));
        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, (await RunAsync(tokenRig, Chat())).Outcome, "The worst-case reservation alone exceeds a 50-token budget.");
        Assert.AreEqual(0, tokenRig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ZeroIsAHardStopNotUnlimited()
    {
        var zero = ModelExecutionLimits.Default with { Background = new ModelBudget(0, 0, 0, 0) };
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")), Configured(zero));

        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, (await RunAsync(rig, Background())).Outcome);
        Assert.AreEqual(0, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ChatAndBackgroundBudgetsAreSeparate()
    {
        var limits = ModelExecutionLimits.Default with { Interactive = ModelExecutionLimits.Default.Interactive with { InstallationDailyRequests = 1 } };
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}", new ModelUsage(5, 5, 10))), Configured(limits));

        Assert.IsTrue((await RunAsync(rig, Chat())).Succeeded);
        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, (await RunAsync(rig, Chat(Bob))).Outcome);
        Assert.IsTrue((await RunAsync(rig, Background())).Succeeded, "Exhausting chat must not block background validation.");
    }

    [TestMethod]
    public async Task TheAllowanceResetsWithTheNextUtcDay()
    {
        var limits = ModelExecutionLimits.Default with { Interactive = ModelExecutionLimits.Default.Interactive with { UserDailyRequests = 1 } };
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}", new ModelUsage(5, 5, 10))), Configured(limits));

        Assert.IsTrue((await RunAsync(rig, Chat())).Succeeded);
        Assert.AreEqual(ModelCallOutcome.BudgetExhausted, (await RunAsync(rig, Chat())).Outcome);
        rig.Clock.UtcNow = Now.AddDays(1);
        Assert.IsTrue((await RunAsync(rig, Chat())).Succeeded);
    }

    [TestMethod]
    public async Task OversizedAndEmptyInputIsRefusedBeforeAnyCall()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")));

        var tooLarge = await RunAsync(rig, Chat(text: new string('x', ModelExecutionLimits.Default.MaxInputCharacters + 1)));
        var empty = await rig.Policy.ExecuteAsync(
            new ModelExecutionRequest(ModelRole.CandidateAmbiguityAssessment, ModelBudgetClass.InteractiveChat, Alice, [], false), AcceptAll, CancellationToken.None);

        Assert.AreEqual(ModelCallOutcome.InputTooLarge, tooLarge.Outcome);
        Assert.AreEqual(ModelCallOutcome.InputTooLarge, empty.Outcome);
        Assert.AreEqual(0, rig.Model.Calls.Count);
        Assert.AreEqual(0, rig.Ledger.Records.Count, "A refusal is not usage.");
    }

    [TestMethod]
    public async Task OutputIsCappedAndACallerCannotRaiseTheCap()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")));

        await rig.Policy.ExecuteAsync(Chat() with { MaxOutputTokens = 64 }, AcceptAll, CancellationToken.None);
        await rig.Policy.ExecuteAsync(Chat() with { MaxOutputTokens = 100_000 }, AcceptAll, CancellationToken.None);

        Assert.AreEqual(64, rig.Model.Calls[0].Request.MaxOutputTokens);
        Assert.AreEqual(ModelExecutionLimits.Default.MaxOutputTokens, rig.Model.Calls[1].Request.MaxOutputTokens);
        Assert.AreEqual(TimeSpan.FromSeconds(ModelExecutionLimits.Default.TimeoutSeconds), rig.Model.Calls[0].Connection.Timeout);
    }

    [TestMethod]
    public async Task InteractiveUseWithoutAPersonToChargeIsRefused()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")));

        var result = await rig.Policy.ExecuteAsync(
            new ModelExecutionRequest(ModelRole.CandidateAmbiguityAssessment, ModelBudgetClass.InteractiveChat, null, [new ModelChatMessage("user", "hi")]),
            AcceptAll, CancellationToken.None);

        Assert.IsTrue(result.WasRefused);
        Assert.AreEqual(0, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task ASecondCallFromTheSamePersonWhileOneIsRunningIsTurnedAway()
    {
        var release = new TaskCompletionSource();
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}", new ModelUsage(5, 5, 10))));
        rig.Model.BeforeAnswer = () => release.Task;

        var first = RunAsync(rig, Chat(Alice));
        while (rig.Model.Calls.Count == 0) await Task.Yield();
        var second = await RunAsync(rig, Chat(Alice));
        var otherPerson = RunAsync(rig, Chat(Bob));
        while (rig.Model.Calls.Count < 2) await Task.Yield();
        var overInstallationLimit = await RunAsync(rig, Chat(Guid.NewGuid()));
        release.SetResult();

        Assert.AreEqual(ModelCallOutcome.ConcurrencyLimited, second.Outcome);
        Assert.AreEqual(ModelCallOutcome.ConcurrencyLimited, overInstallationLimit.Outcome, "Two calls were already running, which is the default installation limit.");
        Assert.IsTrue((await first).Succeeded);
        Assert.IsTrue((await otherPerson).Succeeded);
    }

    [TestMethod]
    public async Task TheCircuitOpensAfterRepeatedProviderFailuresThenAllowsOneTrial()
    {
        var failing = true;
        var rig = new Rig(new ScriptedModel((_, _) => failing ? ScriptedModel.Fail(ModelFailureKind.Unreachable) : ScriptedModel.Ok("{}", new ModelUsage(1, 1, 2))),
            Configured(ModelExecutionLimits.Default with { Interactive = new ModelBudget(1000, 1_000_000, 1000, 1_000_000) }));

        for (var i = 0; i < ModelCircuitBreaker.FailureThreshold; i++)
        {
            Assert.AreEqual(ModelCallOutcome.TransportFailure, (await RunAsync(rig, Chat())).Outcome);
        }

        var callsBefore = rig.Model.Calls.Count;
        Assert.AreEqual(ModelCallOutcome.CircuitOpen, (await RunAsync(rig, Chat())).Outcome);
        Assert.AreEqual(callsBefore, rig.Model.Calls.Count, "An open circuit makes no provider call.");

        rig.Clock.UtcNow = Now + ModelCircuitBreaker.OpenFor + TimeSpan.FromSeconds(1);
        failing = false;
        Assert.IsTrue((await RunAsync(rig, Chat())).Succeeded, "After the pause one trial goes through and closes the circuit.");
        Assert.IsTrue((await RunAsync(rig, Chat())).Succeeded);
    }

    [TestMethod]
    public async Task ModelQualityProblemsDoNotOpenTheCircuit()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("")),
            Configured(ModelExecutionLimits.Default with { Interactive = new ModelBudget(1000, 1_000_000, 1000, 1_000_000) }));

        for (var i = 0; i < ModelCircuitBreaker.FailureThreshold + 3; i++)
        {
            Assert.AreEqual(ModelCallOutcome.EmptyContent, (await RunAsync(rig, Chat())).Outcome);
        }
    }

    [TestMethod]
    public async Task IfUsageCannotBeRecordedNoCallIsMadeAndNothingThrows()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")), ledger: new FakeLedger { FailAdds = true });

        var result = await RunAsync(rig, Chat());

        Assert.AreEqual(ModelCallOutcome.PolicyUnavailable, result.Outcome);
        Assert.AreEqual(0, rig.Model.Calls.Count);
    }

    [TestMethod]
    public async Task AClientThatThrowsBecomesATransportFailureNotAnException()
    {
        var rig = new Rig(new ScriptedModel((_, _) => throw new InvalidOperationException("client bug")));

        var result = await RunAsync(rig, Chat());

        Assert.AreEqual(ModelCallOutcome.TransportFailure, result.Outcome);
        Assert.AreEqual(ModelCallOutcome.TransportFailure, rig.Ledger.Records.Single().Outcome);
    }

    [TestMethod]
    public async Task CancellationPropagatesAndTheReservationIsClosed()
    {
        using var source = new CancellationTokenSource();
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")));
        rig.Model.BeforeAnswer = () =>
        {
            source.Cancel();
            source.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => rig.Policy.ExecuteAsync(Chat(), AcceptAll, source.Token));

        Assert.AreNotEqual(ModelCallOutcome.Pending, rig.Ledger.Records.Single().Outcome);
    }

    [TestMethod]
    public async Task ADisabledRoleOrGatewayMakesNoCallAndRecordsNothing()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Ok("{}")));

        var result = await RunAsync(rig, Chat(role: ModelRole.VisionCoverIdentification));

        Assert.AreEqual(ModelCallOutcome.RoleUnavailable, result.Outcome);
        Assert.AreEqual(0, rig.Model.Calls.Count);
        Assert.AreEqual(0, rig.Ledger.Records.Count);
    }

    [TestMethod]
    public void LimitsValidationRejectsOutOfRangeValuesAndAcceptsTheDefaults()
    {
        Assert.IsNull(ModelExecutionLimits.Default.Validate());
        Assert.IsNotNull((ModelExecutionLimits.Default with { TimeoutSeconds = 1 }).Validate());
        Assert.IsNotNull((ModelExecutionLimits.Default with { MaxConcurrentCalls = 0 }).Validate());
        Assert.IsNotNull((ModelExecutionLimits.Default with { CloudDailyTokens = -1 }).Validate());
        Assert.IsNotNull((ModelExecutionLimits.Default with { Interactive = new ModelBudget(-1, 0, 0, 0) }).Validate());
        Assert.AreEqual(0, ModelExecutionLimits.Default.CloudDailyRequests, "Cloud use is off until an allowance is granted.");
    }


    private static ModelResponse Reply(string? content, string? finish, ModelUsage? usage = null) =>
        new(true, ModelFailureKind.None, null, content, [], finish, "m", usage ?? new ModelUsage(10, 40, 50), 12);

    private static ModelGatewaySettings WithReasoning(ModelReasoningMode mode)
    {
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://litellm.example.test:4000", false, null, Now);
        settings.ConfigureRole(ModelRole.CandidateAmbiguityAssessment, true, ModelPlacement.Local, "local/alias", null, null, Now, mode);
        settings.SetEnabled(true, null, Now);
        return settings;
    }

    [TestMethod]
    public async Task ARoleDefaultsToInheritAndSendsNoReasoningRequest()
    {
        var rig = new Rig(new ScriptedModel((_, _) => Reply("{}", "stop")));

        await RunAsync(rig, Chat());

        Assert.AreEqual(ModelReasoningMode.Inherit, rig.Model.Calls.Single().Request.Reasoning);
    }

    [TestMethod]
    public async Task AnExplicitlyDisabledRoleAsksForNoReasoningAndOtherRolesAreUnaffected()
    {
        var settings = WithReasoning(ModelReasoningMode.Disabled);
        settings.ConfigureRole(ModelRole.ArtifactValidationAssessment, true, ModelPlacement.Local, "local/alias", null, null, Now);
        var rig = new Rig(new ScriptedModel((_, _) => Reply("{}", "stop")), settings);

        await RunAsync(rig, Chat());
        await RunAsync(rig, Background());

        Assert.AreEqual(ModelReasoningMode.Disabled, rig.Model.Calls[0].Request.Reasoning);
        Assert.AreEqual(ModelReasoningMode.Inherit, rig.Model.Calls[1].Request.Reasoning, "Reasoning is per role, never global.");
    }

    [TestMethod]
    public async Task EmptyTextWithoutTruncationEvidenceStaysEmptyContent()
    {
        foreach (var finish in new string?[] { "stop", null })
        {
            var rig = new Rig(new ScriptedModel((_, _) => Reply(string.Empty, finish)));

            var result = await RunAsync(rig, Chat());

            Assert.AreEqual(ModelCallOutcome.EmptyContent, result.Outcome, $"finish={finish ?? "(none)"}: unknown stays unknown.");
        }
    }

    [TestMethod]
    public async Task AReplyThatStoppedAtTheOutputLimitIsClassifiedSeparatelyAndNeverParsed()
    {
        var parsed = 0;
        var rig = new Rig(new ScriptedModel((_, _) => Reply("{\"verdict\":\"Consistent\"", "length")));

        var emptyRig = new Rig(new ScriptedModel((_, _) => Reply(string.Empty, "length")));
        var truncated = await rig.Policy.ExecuteAsync(Chat(), text => { parsed++; return ModelParse.Ok(text); }, CancellationToken.None);
        var emptied = await RunAsync(emptyRig, Chat());

        Assert.AreEqual(ModelCallOutcome.OutputLimitReached, truncated.Outcome);
        Assert.AreEqual(ModelCallOutcome.OutputLimitReached, emptied.Outcome);
        Assert.AreEqual(0, parsed, "A cut-off reply is never handed to the parser, so it cannot be accepted by accident.");
        Assert.IsNull(truncated.Value);
        Assert.AreEqual(50, truncated.Usage!.TotalTokens, "The tokens it spent still count.");
        Assert.AreEqual(ModelCallOutcome.OutputLimitReached, rig.Ledger.Records.Single().Outcome);
    }

    [TestMethod]
    public async Task ARejectedReasoningSettingIsAnUnsupportedCapabilityAndIsNotRetriedOrFallenBack()
    {
        var rig = new Rig(new ScriptedModel((_, _) => ScriptedModel.Fail(ModelFailureKind.UnsupportedCapability, "reasoning not accepted")), WithReasoning(ModelReasoningMode.Disabled));

        var result = await RunAsync(rig, Chat());

        Assert.AreEqual(ModelCallOutcome.UnsupportedCapability, result.Outcome);
        Assert.AreEqual(1, rig.Model.Calls.Count, "One call: no retry with different parameters, no other model.");
        Assert.AreEqual(ModelReasoningMode.Disabled, rig.Model.Calls.Single().Request.Reasoning);
    }
}
