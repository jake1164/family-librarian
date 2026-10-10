using System.Net.WebSockets;
using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Audit;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Relays one administrator's authenticated browser WebSocket to a
/// provider's brokered view socket (docs/04 §8 "Optional interaction
/// view"), for the duration of one job's verification session. FL never
/// interprets the bytes it relays; it only enforces who may connect, that
/// at most one viewer is active per job, and that the session ends when the
/// job stops waiting, expires, is cancelled, or the server shuts down.
/// </summary>
public sealed class ProviderRemoteViewBrokerService(
    IProviderAcquisitionJobStore jobs,
    IExternalProviderStore providers,
    IProviderRemoteViewClient remoteViewClient,
    ICredentialProtector protector,
    RemoteViewSessionRegistry sessions,
    IProviderInteractionClaimStore claims,
    IAuditWriter audit,
    IClock clock)
{
    private static readonly TimeSpan LifecycleCheckInterval = TimeSpan.FromSeconds(5);
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Read-only eligibility check, meant to run <b>before</b> the caller
    /// accepts the WebSocket upgrade, so an ineligible request gets a plain
    /// HTTP status instead of an upgrade immediately followed by a close.
    /// </summary>
    public async Task<RemoteViewEligibility> EvaluateAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);
        if (job is null)
        {
            return RemoteViewEligibility.NotFound;
        }

        if (job.LifecycleState != ProviderAcquisitionJobLifecycleState.Waiting ||
            job.InteractionViewSessionStartedAtUtc is null)
        {
            return RemoteViewEligibility.NotWaiting;
        }

        if (job.InteractionExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow)
        {
            return RemoteViewEligibility.Expired;
        }

        var provider = await providers.FindAsync(job.ExternalProviderId, cancellationToken);
        if (provider is null || !ProviderInteractionFeatures.SupportsInteractionView(provider))
        {
            return RemoteViewEligibility.NotWaiting;
        }

        return RemoteViewEligibility.Eligible;
    }

    /// <summary>
    /// Runs one relay session on an already-accepted browser socket until it
    /// ends, for any reason. Never throws for an ordinary end-of-session; a
    /// caller only needs to handle this returning.
    /// </summary>
    public async Task RunAsync(
        Guid jobId, WebSocket browserSocket, CancellationToken hostShutdownToken,
        Func<CancellationToken, Task<bool>>? authorizationStillValid = null)
    {
        if (!sessions.TryAcquire(jobId, out var closeSignal))
        {
            await CloseQuietly(
                browserSocket, WebSocketCloseStatus.PolicyViolation,
                "Another administrator's view session is already active for this job.", CancellationToken.None);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(closeSignal.Token, hostShutdownToken);
        var reason = "Error";
        IProviderRemoteViewConnection? providerConnection = null;
        Guid providerAuditSubjectId = jobId;

        try
        {
            var job = await jobs.FindAsync(jobId, linked.Token);
            if (job is null || job.LifecycleState != ProviderAcquisitionJobLifecycleState.Waiting ||
                string.IsNullOrWhiteSpace(job.ProviderJobId))
            {
                reason = "NotWaiting";
                await CloseQuietly(
                    browserSocket, WebSocketCloseStatus.PolicyViolation,
                    "This job is no longer waiting for interaction.", CancellationToken.None);
                return;
            }

            var provider = await providers.FindAsync(job.ExternalProviderId, linked.Token);
            if (provider is null)
            {
                reason = "ProviderUnavailable";
                await CloseQuietly(browserSocket, WebSocketCloseStatus.InternalServerError, reason, CancellationToken.None);
                return;
            }

            var apiKey = provider.HasApiKey
                ? protector.Unprotect(ExternalProviderSecretPurposes.ApiKey, provider.ProtectedApiKey!, provider.ApiKeyFormatVersion)
                : null;

            providerConnection = await remoteViewClient.ConnectAsync(
                provider.BaseUrl, apiKey, job.ProviderJobId, linked.Token);

            await audit.WriteAsync(
                AuditActions.ProviderInteractionViewConnected, AuditSubjectTypes.ProviderInteraction, job.Id.ToString(),
                new { job.Id, job.RequestId, job.RequestFormatId, job.ProviderId }, CancellationToken.None);

            await RecordViewerActivityAsync(jobId, CancellationToken.None);

            reason = await PumpAsync(jobId, browserSocket, providerConnection, authorizationStillValid, linked.Token);
        }
        catch (OperationCanceledException)
        {
            reason = hostShutdownToken.IsCancellationRequested ? "ServerShutdown" : "Cancelled";
        }
        catch (Exception)
        {
            reason = "Error";
        }
        finally
        {
            if (providerConnection is not null)
            {
                await audit.WriteAsync(
                    AuditActions.ProviderInteractionViewEnded, AuditSubjectTypes.ProviderInteraction,
                    providerAuditSubjectId.ToString(), new { JobId = providerAuditSubjectId, Reason = reason },
                    CancellationToken.None);
                await providerConnection.DisposeAsync();
                await RecordViewerActivityAsync(jobId, CancellationToken.None);
            }

            sessions.Release(jobId);
            await CloseQuietly(browserSocket, WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
    }

    /// <summary>
    /// Records that a claim's session is (still) alive, at connect and again at
    /// disconnect -- so a claim's lease (HUMAN-ACQ-1) never lapses mid-session and
    /// starts its countdown fresh from the moment the viewer actually left. A
    /// missing claim (in-app sessions predate the claim table; a claim the alert
    /// worker already reaped) is a no-op, not an error.
    /// </summary>
    private async Task RecordViewerActivityAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var claim = await claims.FindAsync(jobId, cancellationToken);
        if (claim is null)
        {
            return;
        }

        claim.RecordViewerActivity(clock.UtcNow);
        await claims.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> PumpAsync(
        Guid jobId, WebSocket browserSocket, IProviderRemoteViewConnection providerConnection,
        Func<CancellationToken, Task<bool>>? authorizationStillValid, CancellationToken sessionToken)
    {
        using var lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);

        var browserToProvider = PumpBrowserToProviderAsync(browserSocket, providerConnection, lifecycleCts.Token);
        var providerToBrowser = PumpProviderToBrowserAsync(providerConnection, browserSocket, lifecycleCts.Token);
        var lifecycleWatch = WatchLifecycleAsync(jobId, authorizationStillValid, lifecycleCts.Token);
        var pumps = new[] { browserToProvider, providerToBrowser, lifecycleWatch };

        var winner = await Task.WhenAny(pumps);
        lifecycleCts.Cancel(); // stop whichever pump(s) didn't finish first
        await Task.WhenAll(pumps.Select(SwallowCancellation));

        return await SwallowCancellation(winner)
            ?? (sessionToken.IsCancellationRequested ? "Cancelled" : "Error");
    }

    private static async Task<string?> SwallowCancellation(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    private static async Task<string> PumpBrowserToProviderAsync(
        WebSocket browserSocket, IProviderRemoteViewConnection providerConnection, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var result = await browserSocket.ReceiveAsync(new Memory<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return "AdminClosed";
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                // docs/04 §8: binary frames only on this connection.
                return "ProtocolViolation";
            }

            await providerConnection.SendAsync(buffer.AsMemory(0, result.Count), result.EndOfMessage, cancellationToken);
        }
    }

    private static async Task<string> PumpProviderToBrowserAsync(
        IProviderRemoteViewConnection providerConnection, WebSocket browserSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var result = await providerConnection.ReceiveAsync(new Memory<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return "ProviderClosed";
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                return "ProtocolViolation";
            }

            await browserSocket.SendAsync(
                buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, result.EndOfMessage, cancellationToken);
        }
    }

    private async Task<string> WatchLifecycleAsync(
        Guid jobId, Func<CancellationToken, Task<bool>>? authorizationStillValid,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(LifecycleCheckInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (authorizationStillValid is not null && !await authorizationStillValid(cancellationToken))
            {
                return "GrantRevoked";
            }

            var job = await jobs.FindAsync(jobId, cancellationToken);
            if (job is null || job.LifecycleState != ProviderAcquisitionJobLifecycleState.Waiting)
            {
                return "JobLeftWaiting";
            }

            if (job.InteractionExpiresAtUtc is { } expiresAt && expiresAt <= clock.UtcNow)
            {
                return "Expired";
            }
        }

        return "Cancelled";
    }

    private static async Task CloseQuietly(
        WebSocket socket, WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, cancellationToken);
            }
        }
#pragma warning disable CA1031 // best-effort cleanup: the socket may already be
        // aborted by the remote side (a dropped connection, a forced
        // lifecycle stop) in ways that surface as any of several exception
        // types depending on the transport -- WebSocketException/
        // ObjectDisposedException/OperationCanceledException from a real
        // socket, IOException wrapping ObjectDisposedException from the
        // in-memory TestServer transport. None of them are a condition the
        // caller needs to react to.
        catch (Exception)
        {
        }
#pragma warning restore CA1031
    }
}

public enum RemoteViewEligibility
{
    Eligible,
    NotFound,
    NotWaiting,
    Expired
}
