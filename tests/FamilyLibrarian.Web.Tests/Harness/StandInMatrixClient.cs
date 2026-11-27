using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>A homeserver that accepts everything and remembers who was invited.</summary>
internal sealed class StandInMatrixClient : IMatrixClient
{
    private int invitations;

    public string? LastInvited { get; private set; }

    public int Invitations => invitations;

    public Task<ConnectionTestOutcome> TestConnectionAsync(
        MatrixSettings settings, string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectionTestOutcome(true, "Connected."));

    public Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
        MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken)
    {
        LastInvited = matrixUserId;
        return Task.FromResult(MatrixRoomResult.Success($"!room{Interlocked.Increment(ref invitations)}:example.test"));
    }

    public Task<SendResult> SendMessageAsync(
        MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken) =>
        Task.FromResult(SendResult.Success());

    public async Task<MatrixSyncResult> SyncAsync(
        MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken)
    {
        // A real sync is a long poll; tests deliver replies straight to the router.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        return MatrixSyncResult.Success(since ?? "start", []);
    }

    public Task<MatrixSendResult> SendRichMessageAsync(
        MatrixSettings settings, string accessToken, string roomId, string plainBody, string htmlBody,
        CancellationToken cancellationToken) => Task.FromResult(MatrixSendResult.Success("$event"));

    public Task<SendResult> EditMessageAsync(
        MatrixSettings settings, string accessToken, string roomId, string eventId, string plainBody,
        string htmlBody, CancellationToken cancellationToken) => Task.FromResult(SendResult.Success());
}
