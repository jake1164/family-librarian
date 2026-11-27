using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Setup;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Domain.Setup;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class MatrixIdentityLinkServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.NewGuid();

    [TestMethod]
    public async Task RequestLinkRejectsAMalformedMatrixId()
    {
        var context = new TestContext();

        context.ConfigureMatrix();

        var result = await context.Service.RequestLinkAsync("not a username", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "cannot contain spaces");
        Assert.IsNull(context.MatrixClient.LastInvited);
    }

    [TestMethod]
    public async Task TheSixthCodeIsRefusedAsALimitAndInvitesNobody()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        // Refused input never reaches the homeserver, so it must not use up the allowance.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await context.Service.RequestLinkAsync("not a username", CancellationToken.None);
        }

        for (var send = 0; send < MatrixCodeSendLimiter.MaxSends; send++)
        {
            var sent = await context.Service.RequestLinkAsync("friend", CancellationToken.None);
            Assert.IsTrue(sent.Succeeded, sent.Error);
            Assert.IsNull(sent.RetryAfter);
        }

        var code = (await context.DestinationStore.FindByUserIdAsync(UserId, CancellationToken.None))!.VerificationCode;
        var refused = await context.Service.RequestLinkAsync("somebody-else", CancellationToken.None);

        Assert.IsFalse(refused.Succeeded);
        Assert.AreEqual(MatrixCodeSendLimiter.Window, refused.RetryAfter);
        StringAssert.Contains(refused.Error, "which is the limit");
        Assert.AreEqual("@friend:example.test", context.MatrixClient.LastInvited);
        // The newest code that was sent still works.
        var destination = (await context.DestinationStore.FindByUserIdAsync(UserId, CancellationToken.None))!;
        Assert.AreEqual(code, destination.VerificationCode);
        Assert.AreEqual("@friend:example.test", destination.MatrixUserId);
        StringAssert.Matches(code, new System.Text.RegularExpressions.Regex("^[0-9]{6}$"));
    }

    [TestMethod]
    public async Task AnIdTooLongToStoreIsRefusedBeforeAnyoneIsInvited()
    {
        var context = new TestContext();
        context.ConfigureMatrix();

        var result = await context.Service.RequestLinkAsync(new string('a', 250), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "too long");
        Assert.IsNull(context.MatrixClient.LastInvited);
    }

    [TestMethod]
    public async Task AUsernameAloneInvitesThatAccountOnTheBotsServer()
    {
        var context = new TestContext();
        context.ConfigureMatrix();

        var result = await context.Service.RequestLinkAsync("Friend", CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual("@friend:example.test", context.MatrixClient.LastInvited);
        Assert.AreEqual("@friend:example.test", result.Status!.MatrixUserId);
        Assert.AreEqual("friend", result.Status.Username);
    }

    [TestMethod]
    public async Task TheSavedServerNameWinsOverTheBotsServer()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        var guidance = await context.GuidanceStore.GetOrCreateAsync(CancellationToken.None);
        Assert.IsNull(guidance.TrySetMessaging("chat.example.test", null, null, null, null, Now));

        var result = await context.Service.RequestLinkAsync("friend", CancellationToken.None);

        Assert.AreEqual("@friend:chat.example.test", context.MatrixClient.LastInvited);
        Assert.AreEqual("friend", result.Status!.Username);
    }

    [TestMethod]
    public async Task AFailedInvitationDoesNotRepeatTheHomeserversErrorToTheMember()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        context.MatrixClient.RoomError = "Could not reach the Matrix homeserver: matrix-internal:8008";

        var result = await context.Service.RequestLinkAsync("friend", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "Could not start a conversation");
        Assert.IsFalse(result.Error!.Contains("matrix-internal", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RequestLinkFailsWhenMatrixIsNotConfigured()
    {
        var context = new TestContext();

        var result = await context.Service.RequestLinkAsync("@friend:example.test", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(MatrixIdentityLinkService.UnavailableMessage, result.Error);
    }

    [TestMethod]
    public async Task StatusReportsWhetherMatrixIsAvailable()
    {
        var context = new TestContext();

        Assert.IsFalse((await context.Service.GetStatusAsync(CancellationToken.None))!.IsAvailable);
        context.ConfigureMatrix();
        Assert.IsTrue((await context.Service.GetStatusAsync(CancellationToken.None))!.IsAvailable);
    }

    [TestMethod]
    public async Task AnExistingLinkCanBeRemovedAfterMatrixIsTurnedOff()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        await context.Service.RequestLinkAsync("@friend:example.test", CancellationToken.None);
        var settings = await context.SettingsStore.GetOrCreateAsync(CancellationToken.None);
        settings.SetEnabled(false, actorUserId: null, Now);

        var result = await context.Service.UnlinkAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Status!.IsAvailable);
        Assert.IsNull(result.Status.MatrixUserId);
    }

    [TestMethod]
    public async Task RequestLinkCreatesAPendingUnverifiedDestinationAndSendsACode()
    {
        var context = new TestContext();
        context.ConfigureMatrix();

        var result = await context.Service.RequestLinkAsync("@friend:example.test", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Status!.IsVerified);
        Assert.IsTrue(result.Status.AwaitingVerification);
        Assert.AreEqual("@friend:example.test", result.Status.MatrixUserId);

        var destination = await context.DestinationStore.FindByUserIdAsync(UserId, CancellationToken.None);
        Assert.IsNotNull(destination);
        Assert.IsFalse(destination!.IsVerified);
        Assert.IsNotNull(destination.VerificationCode);
        StringAssert.Contains(context.MatrixClient.LastMessageSent, destination.VerificationCode);
    }

    [TestMethod]
    public async Task UnlinkRemovesAnExistingVerifiedDestination()
    {
        var context = new TestContext();
        context.ConfigureMatrix();
        await context.Service.RequestLinkAsync("@friend:example.test", CancellationToken.None);
        var destination = await context.DestinationStore.FindByUserIdAsync(UserId, CancellationToken.None);
        destination!.Verify(Now);

        var result = await context.Service.UnlinkAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Status!.IsVerified);
        Assert.IsNull(result.Status.MatrixUserId);
    }

    private sealed class TestContext
    {
        public FakeMatrixSettingsStore SettingsStore { get; } = new();
        public InMemoryUserMatrixDestinationStore DestinationStore { get; } = new();
        public FakeMatrixClient MatrixClient { get; } = new();
        public FakeSetupGuidanceStore GuidanceStore { get; } = new();
        public MatrixIdentityLinkService Service { get; }

        public TestContext()
        {
            Service = new MatrixIdentityLinkService(
                SettingsStore, DestinationStore, MatrixClient, new FakeCredentialProtector(),
                new StubCurrentUser(UserId), new FixedClock(), new NullAuditWriter(), GuidanceStore,
                new MatrixCodeSendLimiter());
        }

        public void ConfigureMatrix()
        {
            var settings = SettingsStore.GetOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
            settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, Now);
            settings.SetAccessToken("protected-token", formatVersion: 1, actorUserId: null, Now);
            settings.SetEnabled(true, actorUserId: null, Now);
        }
    }

    private sealed class FakeSetupGuidanceStore : ISetupGuidanceStore
    {
        private SetupGuidanceSettings? settings;

        public Task<SetupGuidanceSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<SetupGuidanceSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ??= new SetupGuidanceSettings(Now));

        public Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeMatrixSettingsStore : IMatrixSettingsStore
    {
        private MatrixSettings? settings;

        public Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ??= new MatrixSettings(Now));

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryUserMatrixDestinationStore : IUserMatrixDestinationStore
    {
        private readonly Dictionary<Guid, UserMatrixDestination> byUserId = [];

        public Task<UserMatrixDestination?> FindByUserIdAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(byUserId.GetValueOrDefault(userId));

        public Task<UserMatrixDestination?> FindByRoomIdAsync(string roomId, CancellationToken cancellationToken) =>
            Task.FromResult(byUserId.Values.FirstOrDefault(destination => destination.RoomId == roomId));

        public Task<UserMatrixDestination> GetOrCreateForUserAsync(
            Guid userId, DateTimeOffset createdAtUtc, CancellationToken cancellationToken)
        {
            if (!byUserId.TryGetValue(userId, out var destination))
            {
                destination = new UserMatrixDestination(userId, createdAtUtc);
                byUserId[userId] = destination;
            }

            return Task.FromResult(destination);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMatrixClient : IMatrixClient
    {
        public string? LastMessageSent { get; private set; }
        public string? LastInvited { get; private set; }
        public string? RoomError { get; set; }

        public Task<ConnectionTestOutcome> TestConnectionAsync(
            MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestOutcome(true, "ok"));

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
            MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken)
        {
            LastInvited = matrixUserId;
            return Task.FromResult(RoomError is null
                ? MatrixRoomResult.Success("!room:example.test")
                : MatrixRoomResult.Failure(RoomError));
        }

        public Task<SendResult> SendMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
        {
            LastMessageSent = text;
            return Task.FromResult(SendResult.Success());
        }

        public Task<MatrixSyncResult> SyncAsync(
            MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixSyncResult.Success(since, []));

        public Task<MatrixSendResult> SendRichMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SendResult> EditMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody,
            string htmlBody, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;
        public string Protect(string providerId, string plaintext) => plaintext;
        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public Task WriteAsync(string action, string subjectType, string? subjectId, object? detail, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? DisplayName => "Reader";
    }
}
