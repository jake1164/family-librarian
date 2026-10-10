using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

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

        var result = await context.Service.RequestLinkAsync("not-a-matrix-id", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "Matrix ID");
    }

    [TestMethod]
    public async Task RequestLinkFailsWhenMatrixIsNotConfigured()
    {
        var context = new TestContext();

        var result = await context.Service.RequestLinkAsync("@friend:example.test", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "not configured");
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
        public MatrixIdentityLinkService Service { get; }

        public TestContext()
        {
            Service = new MatrixIdentityLinkService(
                SettingsStore, DestinationStore, MatrixClient, new FakeCredentialProtector(),
                new StubCurrentUser(UserId), new FixedClock(), new NullAuditWriter());
        }

        public void ConfigureMatrix()
        {
            var settings = SettingsStore.GetOrCreateAsync(CancellationToken.None).GetAwaiter().GetResult();
            settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, Now);
            settings.SetAccessToken("protected-token", formatVersion: 1, actorUserId: null, Now);
            settings.SetEnabled(true, actorUserId: null, Now);
        }
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

        public Task<ConnectionTestOutcome> TestConnectionAsync(
            MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestOutcome(true, "ok"));

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
            MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixRoomResult.Success("!room:example.test"));

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
