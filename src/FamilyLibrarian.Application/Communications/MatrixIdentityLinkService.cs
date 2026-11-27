using System.Security.Cryptography;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Setup;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;

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
    IAuditWriter audit,
    ISetupGuidanceStore guidanceStore,
    MatrixCodeSendLimiter sendLimiter)
{
    /// <summary>Shown to any signed-in user, so it names who can fix it rather than which settings are missing.</summary>
    public const string UnavailableMessage =
        "Messaging the Librarian isn't available yet. An administrator needs to set it up.";

    private const string ConversationFailedMessage =
        "Could not start a conversation with that username. Check that you have signed in to your messaging app " +
        "and typed the name exactly. If it still fails, ask an administrator.";

    public async Task<MatrixLinkStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId) return null;
        var destination = await destinationStore.FindByUserIdAsync(userId, cancellationToken);
        var matrix = await settingsStore.FindAsync(cancellationToken);
        return ToStatus(destination, IsAvailable(matrix), await ServerNameAsync(matrix, cancellationToken));
    }

    public async Task<MatrixLinkResult> RequestLinkAsync(string matrixUserId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return MatrixLinkResult.Unauthenticated();
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (!IsAvailable(settings))
        {
            return MatrixLinkResult.Invalid(UnavailableMessage);
        }

        var serverName = await ServerNameAsync(settings, cancellationToken);
        if (!MatrixUserIds.TryResolve(matrixUserId, serverName, out var resolved, out var inputError))
        {
            return MatrixLinkResult.Invalid(inputError!);
        }

        var accessToken = protector.Unprotect(
            CommunicationSecretPurposes.MatrixAccessToken, settings!.ProtectedAccessToken!, settings.AccessTokenFormatVersion);
        if (accessToken is null)
        {
            return MatrixLinkResult.Invalid(
                "The Librarian's messaging sign-in could not be read. Ask an administrator to re-enter it.");
        }

        // Counted just before the homeserver is asked to invite anyone, so input refused above costs nothing.
        if (!sendLimiter.TryRecord(userId, clock.UtcNow, out var wait))
        {
            return MatrixLinkResult.LimitReached(wait);
        }

        // The client's error text names the homeserver and its own failure, which a member cannot act on.
        var room = await matrixClient.GetOrCreateDirectRoomAsync(settings, accessToken, resolved, cancellationToken);
        if (!room.Succeeded || room.RoomId is not { } roomId)
        {
            return MatrixLinkResult.Invalid(ConversationFailedMessage);
        }

        var code = GenerateCode();
        var destination = await destinationStore.GetOrCreateForUserAsync(userId, clock.UtcNow, cancellationToken);
        destination.RequestVerification(resolved, roomId, code, clock.UtcNow);
        await destinationStore.SaveChangesAsync(cancellationToken);

        var sendResult = await matrixClient.SendMessageAsync(
            settings, accessToken, roomId,
            $"Your Family Librarian verification code is {code}. Reply with this code to link your account.",
            cancellationToken);
        if (!sendResult.Succeeded)
        {
            return MatrixLinkResult.Invalid(
                "Could not send the code. Try again, and if it still fails ask an administrator.");
        }

        await audit.WriteAsync(
            AuditActions.MatrixIdentityLinkRequested, AuditSubjectTypes.MatrixIdentityLink, userId.ToString(),
            new { UserId = userId }, cancellationToken);

        return MatrixLinkResult.Success(ToStatus(destination, isAvailable: true, serverName));
    }

    public async Task<MatrixLinkResult> UnlinkAsync(CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return MatrixLinkResult.Unauthenticated();
        }

        // Unlinking stays possible while Matrix is unavailable.
        var matrix = await settingsStore.FindAsync(cancellationToken);
        var isAvailable = IsAvailable(matrix);
        var serverName = await ServerNameAsync(matrix, cancellationToken);
        var destination = await destinationStore.FindByUserIdAsync(userId, cancellationToken);
        if (destination is null)
        {
            return MatrixLinkResult.Success(ToStatus(destination, isAvailable, serverName));
        }

        destination.Unlink(clock.UtcNow);
        await destinationStore.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.MatrixIdentityLinkRemoved, AuditSubjectTypes.MatrixIdentityLink, userId.ToString(),
            new { UserId = userId }, cancellationToken);

        return MatrixLinkResult.Success(ToStatus(destination, isAvailable, serverName));
    }

    private async Task<string?> ServerNameAsync(MatrixSettings? matrix, CancellationToken cancellationToken) =>
        SetupGuidanceDefaults.EffectiveServerName(await guidanceStore.FindAsync(cancellationToken), matrix);

    private static bool IsAvailable(MatrixSettings? settings) =>
        settings is { IsEnabled: true, HasAccessToken: true, HomeserverUrl: not null };

    private static MatrixLinkStatus ToStatus(UserMatrixDestination? destination, bool isAvailable, string? serverName) =>
        destination is null
            ? new MatrixLinkStatus(false, null, false, isAvailable, null)
            : new MatrixLinkStatus(destination.IsVerified, destination.MatrixUserId,
                destination.VerificationRequestedAtUtc is not null && !destination.IsVerified, isAvailable,
                destination.MatrixUserId is { } id ? MatrixUserIds.DisplayName(id, serverName) : null);

    private static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <param name="Username">The short form of <paramref name="MatrixUserId"/> to show a person.</param>
public sealed record MatrixLinkStatus(
    bool IsVerified, string? MatrixUserId, bool AwaitingVerification, bool IsAvailable, string? Username);

/// <param name="RetryAfter">Set only when the send limit was reached.</param>
public sealed record MatrixLinkResult(
    bool Succeeded, MatrixLinkStatus? Status, string? Error, TimeSpan? RetryAfter = null)
{
    public static MatrixLinkResult LimitReached(TimeSpan wait) =>
        new(false, null, MatrixCodeSendLimiter.LimitMessage(wait), wait);

    public static MatrixLinkResult Success(MatrixLinkStatus status) => new(true, status, null);

    public static MatrixLinkResult Invalid(string error) => new(false, null, error);

    public static MatrixLinkResult Unauthenticated() => new(false, null, "You must be signed in.");
}
