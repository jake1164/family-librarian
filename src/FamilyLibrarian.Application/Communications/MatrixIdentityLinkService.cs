using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// A household member's own Matrix identity link (COMM-1 §C) -- explicit and
/// revocable per the Communications plan's §14 requirement. Requesting a
/// link starts a DM and sends a one-time code; the member proves ownership
/// by replying with that code in the same room, handled by
/// <see cref="MatrixInboundRouter"/> (step D reuses this same "receive and
/// parse a DM" mechanism, per the plan).
/// </summary>
public sealed class MatrixIdentityLinkService(
    IMatrixSettingsStore settingsStore,
    IUserMatrixDestinationStore destinationStore,
    IMatrixClient matrixClient,
    ICredentialProtector protector,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
{
    public async Task<MatrixLinkStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return null;
        var destination = await destinationStore.FindByUserIdAsync(userId, cancellationToken);
        return ToStatus(destination);
    }

    public async Task<MatrixLinkResult> RequestLinkAsync(string matrixUserId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return MatrixLinkResult.Unauthenticated();
        }

        var trimmed = matrixUserId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !IsValidMatrixId(trimmed))
        {
            return MatrixLinkResult.Invalid("Enter a full Matrix ID, e.g. @you:example.org.");
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true, HasAccessToken: true, HomeserverUrl: not null })
        {
            return MatrixLinkResult.Invalid("Matrix is not configured by an administrator yet.");
        }

        var accessToken = protector.Unprotect(
            CommunicationSecretPurposes.MatrixAccessToken, settings.ProtectedAccessToken!, settings.AccessTokenFormatVersion);
        if (accessToken is null)
        {
            return MatrixLinkResult.Invalid("The stored Matrix bot credential could not be decrypted.");
        }

        var room = await matrixClient.GetOrCreateDirectRoomAsync(settings, accessToken, trimmed, cancellationToken);
        if (!room.Succeeded || room.RoomId is not { } roomId)
        {
            return MatrixLinkResult.Invalid(room.Error ?? "Could not start a Matrix conversation with that ID.");
        }

        var code = GenerateCode();
        var destination = await destinationStore.GetOrCreateForUserAsync(userId, clock.UtcNow, cancellationToken);
        destination.RequestVerification(trimmed, roomId, code, clock.UtcNow);
        await destinationStore.SaveChangesAsync(cancellationToken);

        var sendResult = await matrixClient.SendMessageAsync(
            settings, accessToken, roomId,
            $"Your Family Librarian verification code is {code}. Reply with this code to link your account.",
            cancellationToken);
        if (!sendResult.Succeeded)
        {
            return MatrixLinkResult.Invalid(sendResult.Error ?? "Could not send the verification code.");
        }

        await audit.WriteAsync(
            AuditActions.MatrixIdentityLinkRequested, AuditSubjectTypes.MatrixIdentityLink, userId.ToString(),
            new { UserId = userId }, cancellationToken);

        return MatrixLinkResult.Success(ToStatus(destination)!);
    }

    public async Task<MatrixLinkResult> UnlinkAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return MatrixLinkResult.Unauthenticated();
        }

        var destination = await destinationStore.FindByUserIdAsync(userId, cancellationToken);
        if (destination is null)
        {
            return MatrixLinkResult.Success(new MatrixLinkStatus(false, null, false));
        }

        destination.Unlink(clock.UtcNow);
        await destinationStore.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.MatrixIdentityLinkRemoved, AuditSubjectTypes.MatrixIdentityLink, userId.ToString(),
            new { UserId = userId }, cancellationToken);

        return MatrixLinkResult.Success(ToStatus(destination)!);
    }

    private static MatrixLinkStatus? ToStatus(Domain.Communications.UserMatrixDestination? destination) =>
        destination is null
            ? new MatrixLinkStatus(false, null, false)
            : new MatrixLinkStatus(destination.IsVerified, destination.MatrixUserId, destination.VerificationRequestedAtUtc is not null && !destination.IsVerified);

    private static bool IsValidMatrixId(string value) =>
        value.StartsWith('@') && value.Contains(':') && value.IndexOf(':') > 1 && value.IndexOf(':') < value.Length - 1;

    private static string GenerateCode() =>
        Random.Shared.Next(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record MatrixLinkStatus(bool IsVerified, string? MatrixUserId, bool AwaitingVerification);

public sealed record MatrixLinkResult(bool Succeeded, MatrixLinkStatus? Status, string? Error)
{
    public static MatrixLinkResult Success(MatrixLinkStatus status) => new(true, status, null);

    public static MatrixLinkResult Invalid(string error) => new(false, null, error);

    public static MatrixLinkResult Unauthenticated() => new(false, null, "You must be signed in.");
}
