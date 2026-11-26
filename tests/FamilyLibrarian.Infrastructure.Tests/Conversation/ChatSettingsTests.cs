using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.ChatAdministration;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Conversation;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>The chat switches an administrator sets in the UI, and how they relate to the configured defaults.</summary>
[TestClass]
public sealed class ChatSettingsTests
{
    private static readonly ConversationOptions ConfiguredOff = new();
    private static readonly ConversationOptions ConfiguredOn = new() { Enabled = true, WebEnabled = true, MatrixEnabled = true };

    [TestMethod]
    public async Task WithNothingSavedTheConfiguredSwitchesApply()
    {
        var off = await new ConversationAvailabilityReader(new InMemoryChatSettingsStore(), ConfiguredOff).GetAsync(CancellationToken.None);
        var webOnly = await new ConversationAvailabilityReader(
            new InMemoryChatSettingsStore(), new ConversationOptions { Enabled = true, WebEnabled = true }).GetAsync(CancellationToken.None);
        var masterOff = await new ConversationAvailabilityReader(
            new InMemoryChatSettingsStore(), new ConversationOptions { WebEnabled = true, MatrixEnabled = true }).GetAsync(CancellationToken.None);

        Assert.AreEqual(new ConversationAvailability(false, false), off);
        Assert.AreEqual(new ConversationAvailability(true, false), webOnly);
        Assert.AreEqual(new ConversationAvailability(false, false), masterOff, "The configured master switch still governs the defaults.");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ASavedChoiceWinsOverConfiguration(bool saved)
    {
        var store = new InMemoryChatSettingsStore();
        store.Add(new ChatSettings(saved, saved, null, DateTimeOffset.UnixEpoch));

        var result = await new ConversationAvailabilityReader(store, saved ? ConfiguredOff : ConfiguredOn).GetAsync(CancellationToken.None);

        Assert.AreEqual(new ConversationAvailability(saved, saved), result);
    }

    [TestMethod]
    public async Task ChangingOneSwitchLeavesTheOtherAsItWasRunning()
    {
        var context = new Context(ConfiguredOn);

        var first = await context.Service.UpdateAsync(webEnabled: false, matrixEnabled: null, CancellationToken.None);
        var second = await context.Service.UpdateAsync(webEnabled: null, matrixEnabled: false, CancellationToken.None);

        Assert.AreEqual(new ChatSettingsView(false, true, false), first, "The first save keeps the configured Matrix value.");
        Assert.AreEqual(new ChatSettingsView(false, false, false), second);
        CollectionAssert.AreEqual(
            new[] { AuditActions.ChatSettingsChanged, AuditActions.ChatSettingsChanged }, context.Audit.Actions.ToArray());
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task TheViewSaysWhetherTheMatrixConnectionCanCarryChat(bool connectionEnabled, bool expected)
    {
        var settings = new MatrixSettings(DateTimeOffset.UnixEpoch);
        settings.SetSettings("https://matrix.example.test", "@bot:example.test", null, DateTimeOffset.UnixEpoch);
        settings.SetAccessToken("protected-token", 1, null, DateTimeOffset.UnixEpoch);
        settings.SetEnabled(connectionEnabled, null, DateTimeOffset.UnixEpoch);
        var context = new Context(ConfiguredOff, matrix: settings);

        var view = await context.Service.GetAsync(CancellationToken.None);

        Assert.AreEqual(expected, view!.MatrixConnectionReady);
    }

    [TestMethod]
    public async Task OnlyAnAdministratorCanReadOrChangeTheSwitches()
    {
        var context = new Context(ConfiguredOff, isAdministrator: false);

        Assert.IsNull(await context.Service.GetAsync(CancellationToken.None));
        Assert.IsNull(await context.Service.UpdateAsync(true, true, CancellationToken.None));
        Assert.IsNull(context.Store.Saved);
        Assert.IsEmpty(context.Audit.Actions);
    }

    [TestMethod]
    public async Task TheEngineFollowsTheSavedChoiceFromTheNextMessage()
    {
        var harness = new ConversationHarness(enabled: false);

        var before = await harness.SayAsync("help");
        harness.ChatSettings.Add(new ChatSettings(webEnabled: false, matrixEnabled: true, null, DateTimeOffset.UnixEpoch));
        var matrix = await harness.SayAsync("help", transport: ConversationTransports.Matrix);
        var web = await harness.SayAsync("help", transport: ConversationTransports.Web, scope: "web");

        Assert.AreEqual(ConversationReplyKind.Disabled, before.Reply.Kind);
        Assert.AreNotEqual(ConversationReplyKind.Disabled, matrix.Reply.Kind);
        Assert.AreEqual(ConversationReplyKind.Disabled, web.Reply.Kind);
    }

    private sealed class Context
    {
        public Context(ConversationOptions defaults, bool isAdministrator = true, MatrixSettings? matrix = null)
        {
            Service = new ChatSettingsService(
                Store, new ConversationAvailabilityReader(Store, defaults), new StubMatrixSettingsStore(matrix),
                new FakeCurrentUser(Guid.NewGuid()), new StubAdministrator(isAdministrator), Audit, new FakeClock());
        }

        public InMemoryChatSettingsStore Store { get; } = new();

        public RecordingAuditWriter Audit { get; } = new();

        public ChatSettingsService Service { get; }
    }

    private sealed class StubAdministrator(bool isAdministrator) : ICurrentAdministrator
    {
        public bool IsAdministrator => isAdministrator;
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<string> Actions { get; } = [];

        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private sealed class StubMatrixSettingsStore(MatrixSettings? settings) : IMatrixSettingsStore
    {
        public Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
