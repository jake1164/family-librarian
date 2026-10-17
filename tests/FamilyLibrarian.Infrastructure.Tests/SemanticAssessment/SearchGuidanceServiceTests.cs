using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.ModelGateway;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelGateway;
using FamilyLibrarian.Infrastructure.Tests.ModelTesting;

namespace FamilyLibrarian.Infrastructure.Tests.SemanticAssessment;

[TestClass]
public sealed class SearchGuidanceServiceTests
{
    private const string Good = "{\"schema\":\"search-guidance/1\",\"give_up\":false,\"suggestions\":[{\"reason\":\"alternate_published_title\",\"value\":\"A New Journey\",\"evidence_ids\":[\"req-title\"],\"note\":\"Try a new title.\"}]}";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly BookIdentity Requested = new("The Lantern Road", "Mara Quill", []);

    [TestMethod]
    public async Task IdenticalQuestionsReuseForSevenDaysThenAskAgain()
    {
        var clock = new MutableClock(Now);
        var store = new FakeFindingStore();
        var policy = new Policy(Good);
        var service = new SearchGuidanceService(new Resolver(), policy, store, clock);
        var format = Guid.NewGuid();
        var request = Guid.NewGuid();
        Assert.IsNotNull(await service.GetAsync(request, format, Requested, [], [], clock.UtcNow, CancellationToken.None));
        clock.UtcNow = Now.AddDays(6);
        Assert.IsNotNull(await service.GetAsync(request, format, Requested, [], [], clock.UtcNow, CancellationToken.None));
        Assert.AreEqual(1, policy.Calls);
        clock.UtcNow = Now.AddDays(7);
        Assert.IsNotNull(await service.GetAsync(request, format, Requested, [], [], clock.UtcNow, CancellationToken.None));
        Assert.AreEqual(2, policy.Calls);
        Assert.HasCount(2, store.Findings);
        Assert.IsTrue(store.Findings.All(f => f.Model == "fake" && f.Role == ModelRole.SearchGuidance && f.SchemaId == "search-guidance/1"));
    }

    [TestMethod]
    [DataRow("{\"schema\":\"search-guidance/1\",\"give_up\":true,\"suggestions\":[]}")]
    [DataRow("invalid JSON")]
    public async Task ChangedEvidenceCannotCauseASecondCallWithinTheSameCycle(string reply)
    {
        var clock = new MutableClock(Now);
        var policy = new Policy(reply);
        var service = new SearchGuidanceService(new Resolver(), policy, new FakeFindingStore(), clock);
        var format = Guid.NewGuid();
        var request = Guid.NewGuid();
        await service.GetAsync(request, format, Requested, [], [], Now, CancellationToken.None);
        clock.UtcNow = Now.AddSeconds(1);
        await service.GetAsync(request, format, Requested with { Title = "Changed" }, [], [], Now, CancellationToken.None);
        Assert.AreEqual(1, policy.Calls);
    }

    [TestMethod]
    public async Task InvalidJsonIsReusedButRefusalsCanBeRetriedInANewCycle()
    {
        var clock = new MutableClock(Now);
        var store = new FakeFindingStore();
        var policy = new Policy("invalid");
        var service = new SearchGuidanceService(new Resolver(), policy, store, clock);
        var request = Guid.NewGuid();
        var format = Guid.NewGuid();
        await service.GetAsync(request, format, Requested, [], [], Now, CancellationToken.None);
        clock.UtcNow = Now.AddDays(1);
        Assert.IsNull(await service.GetAsync(request, format, Requested, [], [], clock.UtcNow, CancellationToken.None));
        Assert.AreEqual(1, policy.Calls);
        Assert.AreEqual(ModelCallOutcome.InvalidJson, store.Findings.Single().Outcome);

        var refused = new Policy(Good) { Refuse = true };
        var refusedStore = new FakeFindingStore();
        var refusedService = new SearchGuidanceService(new Resolver(), refused, refusedStore, clock);
        await refusedService.GetAsync(request, format, Requested, [], [], clock.UtcNow, CancellationToken.None);
        clock.UtcNow = Now.AddDays(2);
        await refusedService.GetAsync(request, format, Requested, [], [], clock.UtcNow, CancellationToken.None);
        Assert.AreEqual(2, refused.Calls);
        Assert.HasCount(2, refusedStore.Findings);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesAndDoesNotApplyAProposal()
    {
        using var caller = new CancellationTokenSource();
        var policy = new Policy(Good) { BeforeParse = token => { caller.Cancel(); token.ThrowIfCancellationRequested(); } };
        var service = new SearchGuidanceService(new Resolver(), policy, new FakeFindingStore(), new MutableClock(Now));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.GetAsync(Guid.NewGuid(), Guid.NewGuid(),
            Requested, [], [], Now, caller.Token));
    }

    [TestMethod]
    public async Task StorageFailureCannotApplyEvenAValidProposal()
    {
        var policy = new Policy(Good);
        var service = new SearchGuidanceService(new Resolver(), policy, new FakeFindingStore { FailAdds = true }, new MutableClock(Now));
        Assert.IsNull(await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), Requested, [], [], Now, CancellationToken.None));
        Assert.AreEqual(1, policy.Calls);
    }

    [TestMethod]
    public async Task AnUnapplicableMoveRejectsTheWholeReplyBeforeAnyProposalIsApplied()
    {
        var policy = new Policy(Good.Replace("alternate_published_title", "series_naming", StringComparison.Ordinal));
        var store = new FakeFindingStore();
        var service = new SearchGuidanceService(new Resolver(), policy, store, new MutableClock(Now));
        Assert.IsNull(await service.GetAsync(Guid.NewGuid(), Guid.NewGuid(), Requested, [], [], Now, CancellationToken.None));
        Assert.AreEqual(ModelCallOutcome.SchemaRejected, store.Findings.Single().Outcome);
    }

    [TestMethod]
    public async Task UnconfiguredRoleIsOffAndOnlyPhaseSixRolesWereAppended()
    {
        var settings = new ModelGatewaySettings(Now);
        settings.Configure("http://model.test", false, null, Now);
        settings.SetEnabled(true, null, Now);
        var resolver = new ModelRoleResolver(new FakeGatewayStore { Settings = settings }, new PassThroughProtector());
        Assert.IsFalse((await resolver.ResolveAsync(ModelRole.SearchGuidance, CancellationToken.None)).IsAvailable);
        Assert.HasCount(6, Enum.GetValues<ModelRole>());
        Assert.HasCount(3, Enum.GetValues<FamilyLibrarian.Domain.Evidence.SemanticSubjectKind>());
    }

    private sealed class Resolver : IModelRoleResolver
    {
        public Task<ModelRoleResolution> ResolveAsync(ModelRole role, CancellationToken cancellationToken) => Task.FromResult(
            new ModelRoleResolution(role, ModelUnavailableReason.None, new Uri("http://model.test"), null, "fake", false, ModelExecutionLimits.Default));
    }

    private sealed class Policy(string reply) : IModelExecutionPolicy
    {
        public int Calls { get; private set; }
        public bool Refuse { get; init; }
        public Action<CancellationToken>? BeforeParse { get; init; }
        public Task<ModelExecution<T>> ExecuteAsync<T>(ModelExecutionRequest request, Func<string, ModelParseResult<T>> parse, CancellationToken cancellationToken)
        {
            Calls++;
            BeforeParse?.Invoke(cancellationToken);
            if (Refuse) return Task.FromResult(new ModelExecution<T>(ModelCallOutcome.BudgetExhausted, default, "Budget", null, 0, "fake", false));
            var result = parse(reply);
            return Task.FromResult(new ModelExecution<T>(result.Failure, result.Value, result.Detail, null, 0, "fake", false));
        }
    }
}
