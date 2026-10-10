using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class MatrixSettingsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MatrixCannotBeEnabledUntilTheSavedConfigurationPassesATest()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var beforeTest = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsFalse(beforeTest.Succeeded);
        StringAssert.Contains(beforeTest.Error, "successful test message");

        var test = await context.Service.SendTestAsync(null, null, null, CancellationToken.None);
        Assert.IsTrue(test.Succeeded);
        Assert.IsTrue(test.Outcome!.Succeeded);

        var enabled = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsTrue(enabled.Succeeded);
        Assert.IsTrue(enabled.Status!.IsEnabled);
    }

    [TestMethod]
    public async Task ChangingTheSavedSettingsInvalidatesTheLastSuccessfulTest()
    {
        var context = new TestContext();
        await context.ConfigureAsync();
        await context.Service.SendTestAsync(null, null, null, CancellationToken.None);
        await context.Service.SetEnabledAsync(true, CancellationToken.None);

        await context.Service.SetSettingsAsync("https://matrix.other.example.test", "@bot:example.test", CancellationToken.None);

        var enabled = await context.Service.SetEnabledAsync(true, CancellationToken.None);
        Assert.IsFalse(enabled.Succeeded);
        StringAssert.Contains(enabled.Error, "successful test message");
    }

    [TestMethod]
    public async Task InvalidHomeserverUrlIsRejected()
    {
        var context = new TestContext();

        var result = await context.Service.SetSettingsAsync("not-a-url", "@bot:example.test", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "homeserver URL");
    }

    [TestMethod]
    public async Task SendTestAsyncFallsBackToTheSavedAccessTokenWhenDraftIsBlank()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync(null, null, accessToken: null, CancellationToken.None);

        Assert.IsTrue(test.Succeeded);
        Assert.AreEqual("super-secret-token", context.MatrixClient.LastAccessTokenUsed);
    }

    [TestMethod]
    public async Task SendTestAsyncUsesTheDraftAccessTokenWhenProvided()
    {
        var context = new TestContext();
        await context.ConfigureAsync();

        var test = await context.Service.SendTestAsync(null, null, "freshly-typed-token", CancellationToken.None);

        Assert.IsTrue(test.Succeeded);
        Assert.AreEqual("freshly-typed-token", context.MatrixClient.LastAccessTokenUsed);
    }

    private sealed class TestContext
    {
        public FakeMatrixSettingsStore Store { get; } = new();
        public FakeMatrixClient MatrixClient { get; } = new();
        public MatrixSettingsService Service { get; }

        public TestContext()
        {
            Service = new MatrixSettingsService(
                Store, new FakeCredentialProtector(), MatrixClient, new NullAuditWriter(), new StubCurrentUser(), new FixedClock());
        }

        public async Task ConfigureAsync()
        {
            await Service.SetSettingsAsync("https://matrix.example.test", "@bot:example.test", CancellationToken.None);
            await Service.SetAccessTokenAsync("super-secret-token", CancellationToken.None);
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

    private sealed class FakeMatrixClient : IMatrixClient
    {
        public string? LastAccessTokenUsed { get; private set; }

        public Task<ConnectionTestOutcome> TestConnectionAsync(
            MatrixSettings settings, string accessToken, CancellationToken cancellationToken)
        {
            LastAccessTokenUsed = accessToken;
            return Task.FromResult(new ConnectionTestOutcome(true, "Connected as @bot:example.test."));
        }

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
            MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixRoomResult.Success("!room:example.test"));

        public Task<SendResult> SendMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken) =>
            Task.FromResult(SendResult.Success());

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

    private sealed class StubCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
        public string? DisplayName => null;
    }
}
