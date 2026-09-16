using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Handles one inbound Matrix DM (COMM-1 §D). Two, and only two, things a
/// message can be: the reply to a pending verification code (§C), or a
/// response to the linked user's most recent promptable ask. Everything
/// else -- an unrecognized reply, or nothing outstanding to resolve it
/// against -- gets a generic reply pointing to the web app, never silence
/// and never an attempt at general parsing. Adding a future promptable type
/// (P1-4's series-following example in the plan) means adding another case
/// here, not a router redesign.
/// </summary>
public sealed class MatrixInboundRouter(
    IUserMatrixDestinationStore destinationStore,
    IOutboundCommunicationStore communicationStore,
    DeliveryAttemptService deliveryAttempts,
    IMatrixSettingsStore settingsStore,
    ICredentialProtector protector,
    IMatrixClient matrixClient,
    IClock clock,
    IAuditWriter audit)
{
    private static readonly string[] AffirmativeReplies = ["yes", "yep", "y"];
    private static readonly string[] NegativeReplies = ["no", "nope", "n"];

    public async Task HandleMessageAsync(MatrixInboundMessage message, CancellationToken cancellationToken)
    {
        var destination = await destinationStore.FindByRoomIdAsync(message.RoomId, cancellationToken);
        if (destination is null)
        {
            // An unknown room: not a linked household member's DM. Never a valid target to reply into blind.
            return;
        }

        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true, HasAccessToken: true })
        {
            return;
        }

        var accessToken = protector.Unprotect(
            CommunicationSecretPurposes.MatrixAccessToken, settings.ProtectedAccessToken!, settings.AccessTokenFormatVersion);
        if (accessToken is null)
        {
            return;
        }

        var body = message.Body.Trim();

        if (!destination.IsVerified)
        {
            await HandleVerificationReplyAsync(destination, body, settings, accessToken, cancellationToken);
            return;
        }

        await HandlePromptableReplyAsync(destination, body, settings, accessToken, cancellationToken);
    }

    private async Task HandleVerificationReplyAsync(
        UserMatrixDestination destination, string body, MatrixSettings settings, string accessToken,
        CancellationToken cancellationToken)
    {
        if (destination.VerificationCode is null ||
            !string.Equals(body, destination.VerificationCode, StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(settings, accessToken, destination.RoomId!,
                "That code didn't match. Request a new one from Family Librarian's account settings.", cancellationToken);
            return;
        }

        destination.Verify(clock.UtcNow);
        await destinationStore.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            AuditActions.MatrixIdentityLinkVerified, AuditSubjectTypes.MatrixIdentityLink, destination.UserId.ToString(),
            new { destination.UserId }, cancellationToken);

        await ReplyAsync(settings, accessToken, destination.RoomId!,
            "You're linked! Family Librarian notifications will reach you here.", cancellationToken);
    }

    private async Task HandlePromptableReplyAsync(
        UserMatrixDestination destination, string body, MatrixSettings settings, string accessToken,
        CancellationToken cancellationToken)
    {
        var isAffirmative = AffirmativeReplies.Contains(body, StringComparer.OrdinalIgnoreCase);
        var isNegative = !isAffirmative && NegativeReplies.Contains(body, StringComparer.OrdinalIgnoreCase);
        if (!isAffirmative && !isNegative)
        {
            await ReplyAsync(settings, accessToken, destination.RoomId!,
                "I didn't understand that. Open Family Librarian's Requests page for the full picture.", cancellationToken);
            return;
        }

        var ask = await communicationStore.FindMostRecentByTypeAsync(
            destination.UserId, OutboundCommunicationTypes.KindleDeliveryConfirmationRequested, cancellationToken);
        if (ask?.RelatedEntityId is not { } attemptId)
        {
            await ReplyAsync(settings, accessToken, destination.RoomId!,
                "There's nothing waiting on a confirmation right now. Open Family Librarian's Requests page for the full picture.",
                cancellationToken);
            return;
        }

        var result = isAffirmative
            ? await deliveryAttempts.ConfirmReceivedAsync(attemptId, destination.UserId, cancellationToken)
            : await deliveryAttempts.ReportMissingAsync(attemptId, destination.UserId, cancellationToken);

        var reply = result.Outcome switch
        {
            ConfirmDeliveryOutcome.Success when isAffirmative => "Thanks, marked as delivered!",
            ConfirmDeliveryOutcome.Success => "Thanks -- we'll take a look and retry.",
            _ => "That's already been handled. Open Family Librarian's Requests page for the full picture.",
        };
        await ReplyAsync(settings, accessToken, destination.RoomId!, reply, cancellationToken);
    }

    private Task<SendResult> ReplyAsync(
        MatrixSettings settings, string accessToken, string roomId, string text, CancellationToken cancellationToken) =>
        matrixClient.SendMessageAsync(settings, accessToken, roomId, text, cancellationToken);
}
