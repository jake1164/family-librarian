using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Communications;

namespace FamilyLibrarian.Infrastructure.Tests.Communications;

[TestClass]
public sealed class MatrixOutboundCommunicationProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task IsDisabledWhenNoSettingsHaveBeenSaved()
    {
        var provider = new MatrixOutboundCommunicationProvider(
            new FakeMatrixSettingsStore(null), new FakeDestinationLookup(), new FakeCredentialProtector(), new FakeMatrixClient());

        Assert.IsFalse(await provider.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IsEnabledWhenSettingsAreEnabled()
    {
        var settings = ConfiguredSettings();
        var provider = new MatrixOutboundCommunicationProvider(
            new FakeMatrixSettingsStore(settings), new FakeDestinationLookup(), new FakeCredentialProtector(), new FakeMatrixClient());

        Assert.IsTrue(await provider.IsEnabledAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SendFailsWhenTheRecipientHasNoVerifiedDestination()
    {
        var settings = ConfiguredSettings();
        var provider = new MatrixOutboundCommunicationProvider(
            new FakeMatrixSettingsStore(settings), new FakeDestinationLookup(roomId: null), new FakeCredentialProtector(), new FakeMatrixClient());
        var communication = new OutboundCommunication(
            Guid.NewGuid(), OutboundCommunicationTypes.RequestStatusChanged, "Body", "Subject", null, null, null, Now);

        var result = await provider.SendAsync(communication, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Error, "verified Matrix destination");
    }

    [TestMethod]
    public async Task SendDeliversToTheVerifiedRoom()
    {
        var settings = ConfiguredSettings();
        var client = new FakeMatrixClient();
        var provider = new MatrixOutboundCommunicationProvider(
            new FakeMatrixSettingsStore(settings), new FakeDestinationLookup("!room:example.test"), new FakeCredentialProtector(), client);
        var communication = new OutboundCommunication(
            Guid.NewGuid(), OutboundCommunicationTypes.RequestStatusChanged, "Your book is ready.", "Subject", null, null, null, Now);

        var result = await provider.SendAsync(communication, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("!room:example.test", client.LastRoomId);
        Assert.AreEqual("Your book is ready.", client.LastText);
    }

    private static MatrixSettings ConfiguredSettings()
    {
        var settings = new MatrixSettings(Now);
        settings.SetSettings("https://matrix.example.test", "@bot:example.test", actorUserId: null, Now);
        settings.SetAccessToken("protected-token", formatVersion: 1, actorUserId: null, Now);
        settings.SetEnabled(true, actorUserId: null, Now);
        return settings;
    }

    private sealed class FakeMatrixSettingsStore(MatrixSettings? settings) : IMatrixSettingsStore
    {
        public Task<MatrixSettings?> FindAsync(CancellationToken cancellationToken) => Task.FromResult(settings);

        public Task<MatrixSettings> GetOrCreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(settings ?? throw new InvalidOperationException());

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeDestinationLookup(string? roomId = "!room:example.test") : IUserMatrixDestinationLookup
    {
        public Task<string?> GetVerifiedRoomIdAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(roomId);
    }

    private sealed class FakeCredentialProtector : ICredentialProtector
    {
        public int FormatVersion => 1;
        public string Protect(string providerId, string plaintext) => plaintext;
        public string? Unprotect(string providerId, string protectedValue, int formatVersion) => protectedValue;
    }

    private sealed class FakeMatrixClient : IMatrixClient
    {
        public string? LastRoomId { get; private set; }
        public string? LastText { get; private set; }

        public Task<ConnectionTestOutcome> TestConnectionAsync(
            MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectionTestOutcome(true, "ok"));

        public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
            MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken) =>
            Task.FromResult(MatrixRoomResult.Success("!room:example.test"));

        public Task<SendResult> SendMessageAsync(
            MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken)
        {
            LastRoomId = roomId;
            LastText = text;
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
}
