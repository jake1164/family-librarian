using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// The Matrix Client-Server API surface COMM-1 needs, kept as one seam so
/// nothing above this layer knows it's raw HTTP against an admin-configured
/// homeserver -- the same "resolve settings fresh, don't cache" posture as
/// <c>CwaCatalogClient</c>, since the homeserver URL and bot token can change
/// at any time. The caller always supplies the already-decrypted access
/// token; this interface never touches <see cref="Integrations.ICredentialProtector"/>.
/// </summary>
public interface IMatrixClient
{
    /// <summary>A cheap identity probe (<c>/account/whoami</c>) -- the "Test Matrix connection" action.</summary>
    Task<ConnectionTestOutcome> TestConnectionAsync(
        MatrixSettings settings, string accessToken, CancellationToken cancellationToken);

    /// <summary>
    /// Starts (or reuses) a direct-message room with <paramref name="matrixUserId"/>.
    /// The bot is always a member of a room it creates, so no separate join step is needed.
    /// </summary>
    Task<MatrixRoomResult> GetOrCreateDirectRoomAsync(
        MatrixSettings settings, string accessToken, string matrixUserId, CancellationToken cancellationToken);

    Task<SendResult> SendMessageAsync(
        MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken);

    /// <summary>
    /// One <c>/sync</c> long-poll pass across every room the bot belongs to.
    /// <paramref name="since"/> is <see langword="null"/> only on the very
    /// first call; every call after that resumes from the previous result's
    /// <see cref="MatrixSyncResult.NextBatch"/>, so a message is never seen twice.
    /// </summary>
    Task<MatrixSyncResult> SyncAsync(
        MatrixSettings settings, string accessToken, string? since, CancellationToken cancellationToken);
}

public sealed record MatrixRoomResult(bool Succeeded, string? RoomId, string? Error)
{
    public static MatrixRoomResult Success(string roomId) => new(true, roomId, null);

    public static MatrixRoomResult Failure(string error) => new(false, null, error);
}

public sealed record MatrixInboundMessage(string RoomId, string SenderUserId, string Body);

public sealed record MatrixSyncResult(bool Succeeded, string? NextBatch, IReadOnlyList<MatrixInboundMessage> Messages, string? Error)
{
    public static MatrixSyncResult Success(string? nextBatch, IReadOnlyList<MatrixInboundMessage> messages) =>
        new(true, nextBatch, messages, null);

    public static MatrixSyncResult Failure(string error) => new(false, null, [], error);
}
