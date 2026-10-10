using FamilyLibrarian.Application.Acquisition;

namespace FamilyLibrarian.Web.Endpoints;

/// <summary>
/// The brokered remote-view WebSocket relay (HUMAN-ACQ-1 Phase 3, docs/04 §8
/// "Optional interaction view"), shared by the authenticated admin route
/// (<c>AdminRequestEndpoints</c>) and the magic-link grant route
/// (<c>InteractionLinkEndpoints</c>) — one relay implementation, not two. Each
/// caller does its own authorization/claim check before calling
/// <see cref="HandleAsync"/>; this type only evaluates job eligibility and runs
/// the socket.
/// </summary>
internal static class RemoteViewSocketHandler
{
    public static async Task<IResult> HandleAsync(
        Guid jobId,
        HttpContext context,
        ProviderRemoteViewBrokerService broker,
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? authorizationStillValid = null)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new { message = "This route only accepts a WebSocket upgrade." });
        }

        var eligibility = await broker.EvaluateAsync(jobId, cancellationToken);
        switch (eligibility)
        {
            case RemoteViewEligibility.NotFound:
                return Results.NotFound();
            case RemoteViewEligibility.NotWaiting:
                return Results.Conflict(new
                {
                    message = "This provider acquisition is not currently offering a remote view. Reload the queue."
                });
            case RemoteViewEligibility.Expired:
                return Results.Conflict(new
                {
                    message = "This provider interaction has expired. Reload the queue before choosing a new action."
                });
        }

        using var browserSocket = await context.WebSockets.AcceptWebSocketAsync();
        await broker.RunAsync(jobId, browserSocket, lifetime.ApplicationStopping, authorizationStillValid);
        return Results.Empty;
    }
}
