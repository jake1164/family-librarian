using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Domain.Communications;

namespace FamilyLibrarian.Infrastructure.Communications;

/// <summary>
/// The Matrix send path (COMM-1 §B): translates a normalized
/// <see cref="OutboundCommunication"/> into a DM through the
/// administrator-configured bot, mirroring <c>SmtpOutboundCommunicationProvider</c>'s
/// structure exactly. Registering this alongside the SMTP provider is the
/// entire "make Matrix a second transport" story -- <c>OutboundCommunicationDispatcher</c>
/// already fans every communication out to every enabled
/// <see cref="IOutboundCommunicationProvider"/> with no dispatcher change required.
/// </summary>
public sealed class MatrixOutboundCommunicationProvider(
    IMatrixSettingsStore settingsStore,
    IUserMatrixDestinationLookup destinationLookup,
    ICredentialProtector protector,
    IMatrixClient matrixClient) : IOutboundCommunicationProvider
{
    public string ProviderId => "matrix";

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        return settings?.IsEnabled == true;
    }

    public async Task<SendResult> SendAsync(OutboundCommunication communication, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is not { IsEnabled: true, HasAccessToken: true, HomeserverUrl: not null })
        {
            return SendResult.Failure("Matrix is not fully configured.");
        }

        var roomId = await destinationLookup.GetVerifiedRoomIdAsync(communication.RecipientUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return SendResult.Failure("The recipient has no verified Matrix destination.");
        }

        var accessToken = protector.Unprotect(
            CommunicationSecretPurposes.MatrixAccessToken, settings.ProtectedAccessToken!, settings.AccessTokenFormatVersion);
        if (accessToken is null)
        {
            return SendResult.Failure("The stored Matrix access token could not be decrypted.");
        }

        return await matrixClient.SendMessageAsync(settings, accessToken, roomId, BuildBody(communication), cancellationToken);
    }

    private static string BuildBody(OutboundCommunication communication) =>
        communication.Link is null ? communication.Body : $"{communication.Body}{Environment.NewLine}{Environment.NewLine}{communication.Link}";
}
