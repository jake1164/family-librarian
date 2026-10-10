using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Delivery;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// Handles one inbound Matrix DM (COMM-1 §D): a reply to a pending verification
/// code, a response to the linked user's most recent promptable ask, or an
/// administrator's FALLBACK reply to an exact provider alert event. Everything
/// else gets a generic reply pointing to the web app; this is a fixed command
/// set, not a general parser.
/// </summary>
public sealed class MatrixInboundRouter(
    IUserMatrixDestinationStore destinationStore,
    IOutboundCommunicationStore communicationStore,
    DeliveryAttemptService deliveryAttempts,
    IMatrixSettingsStore settingsStore,
    ICredentialProtector protector,
    IMatrixClient matrixClient,
    IClock clock,
    IAuditWriter audit,
    ProviderInteractionLinkService? providerInteractions = null)
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

        var body = CommandBody(message);

        if (!destination.IsVerified)
        {
            await HandleVerificationReplyAsync(destination, body, settings, accessToken, cancellationToken);
            return;
        }

        if (string.Equals(body, "fallback", StringComparison.OrdinalIgnoreCase))
        {
            await HandleProviderFallbackReplyAsync(
                destination, message, settings, accessToken, cancellationToken);
            return;
        }

        await HandlePromptableReplyAsync(destination, body, settings, accessToken, cancellationToken);
    }

    private async Task HandleProviderFallbackReplyAsync(
        UserMatrixDestination destination, MatrixInboundMessage message, MatrixSettings settings,
        string accessToken, CancellationToken cancellationToken)
    {
        if (!string.Equals(destination.MatrixUserId, message.SenderUserId, StringComparison.Ordinal))
        {
            await ReplyAsync(settings, accessToken, destination.RoomId!,
                "Only the linked Matrix account can choose a provider fallback.", cancellationToken);
            return;
        }

        if (providerInteractions is null)
        {
            await ReplyAsync(settings, accessToken, destination.RoomId!,
                "Provider fallback replies are not available right now. Open Family Librarian's provider interactions page.",
                cancellationToken);
            return;
        }

        var result = await providerInteractions.UseFallbackFromMatrixReplyAsync(
            destination.UserId, message.RoomId, message.ReplyToEventId, cancellationToken);
        var reply = result.Kind switch
        {
            InteractionLinkClaimOutcomeKind.Claimed when !result.ProviderUnavailable =>
                $"The provider fallback was selected for {Quote(result.WorkTitle)}. The request will continue if the provider can fulfill it.",
            InteractionLinkClaimOutcomeKind.Claimed =>
                $"The fallback action was sent for {Quote(result.WorkTitle)}, but the provider could not confirm it. Open Family Librarian's provider interactions page to check the request.",
            InteractionLinkClaimOutcomeKind.ClaimedByOther =>
                $"Another administrator is handling this provider interaction ({result.ClaimedByDisplayName}).",
            InteractionLinkClaimOutcomeKind.NothingWaiting or InteractionLinkClaimOutcomeKind.Done =>
                "That provider interaction is no longer waiting for an action.",
            InteractionLinkClaimOutcomeKind.Expired =>
                "That provider alert has expired. Open Family Librarian to review the current request.",
            _ => "I couldn't match that reply to an active provider alert. Reply FALLBACK to the current alert message, or open Family Librarian's provider interactions page."
        };
        await ReplyAsync(settings, accessToken, destination.RoomId!, reply, cancellationToken);
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

    private static string Quote(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "the waiting request" : $"\"{title}\"";

    private static string CommandBody(MatrixInboundMessage message)
    {
        var body = message.Body.Trim();
        if (string.IsNullOrWhiteSpace(message.ReplyToEventId)) return body;

        // Matrix clients commonly prepend the quoted alert to m.text when
        // replying. Parse the text after that quote so a normal one-word reply
        // still works; the event relation remains the actual authorization
        // binding, never the quoted body itself.
        var separator = body.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator <= 0) return body;
        var quote = body[..separator].Split('\n');
        return quote.All(line => line.TrimStart().StartsWith('>'))
            ? body[(separator + 2)..].Trim()
            : body;
    }
}
