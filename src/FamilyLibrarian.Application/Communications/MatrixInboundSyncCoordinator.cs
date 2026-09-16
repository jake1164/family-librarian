using FamilyLibrarian.Application.Integrations;

namespace FamilyLibrarian.Application.Communications;

/// <summary>
/// One <c>/sync</c> pass: resolve the admin-configured bot, poll, route every
/// message through <see cref="MatrixInboundRouter"/>, then persist the
/// resulting cursor so the next pass never re-processes a message. Driven by
/// a hosted service the same way <see cref="OutboundCommunicationDispatcher"/> is.
/// </summary>
public sealed class MatrixInboundSyncCoordinator(
    IMatrixSettingsStore settingsStore,
    ICredentialProtector protector,
    IMatrixClient matrixClient,
    MatrixInboundRouter router)
{
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetOrCreateAsync(cancellationToken);
        if (!settings.IsEnabled || !settings.HasAccessToken)
        {
            return 0;
        }

        var accessToken = protector.Unprotect(
            CommunicationSecretPurposes.MatrixAccessToken, settings.ProtectedAccessToken!, settings.AccessTokenFormatVersion);
        if (accessToken is null)
        {
            return 0;
        }

        var sync = await matrixClient.SyncAsync(settings, accessToken, settings.LastSyncToken, cancellationToken);
        if (!sync.Succeeded)
        {
            return 0;
        }

        foreach (var message in sync.Messages)
        {
            await router.HandleMessageAsync(message, cancellationToken);
        }

        if (sync.NextBatch is not null)
        {
            settings.RecordSyncToken(sync.NextBatch);
            await settingsStore.SaveChangesAsync(cancellationToken);
        }

        return sync.Messages.Count;
    }
}
