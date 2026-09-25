using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Acquisition;

namespace FamilyLibrarian.Web.Client.Acquisition;

/// <summary>
/// Anonymous client for HUMAN-ACQ-1's magic-link claim/status/view routes. No
/// antiforgery header: the claim endpoint carries no ambient credential to
/// forge with (the token itself is the credential), and the grant-protected
/// status/view routes are authenticated by the cookie <c>ClaimAsync</c> causes
/// the server to issue, which the browser then attaches automatically.
/// </summary>
public sealed class InteractionLinkApiClient(HttpClient httpClient)
{
    private const string BasePath = "api/v1/interaction-links";

    public async Task<InteractionLinkClaimOutcome> ClaimAsync(string token, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"{BasePath}/claim", new ClaimInteractionLinkRequest(token), cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<ClaimInteractionLinkResponse>(cancellationToken);
            return body is null ? InteractionLinkClaimOutcome.Invalid() : InteractionLinkClaimOutcome.Claimed(body);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return InteractionLinkClaimOutcome.Invalid();
        }

        var (reason, claimedBy) = await ReadReasonAsync(response, cancellationToken);
        return reason switch
        {
            "claimed" => InteractionLinkClaimOutcome.ClaimedByOther(claimedBy),
            "done" => InteractionLinkClaimOutcome.Done(),
            "nothing-waiting" => InteractionLinkClaimOutcome.NothingWaiting(),
            "expired" => InteractionLinkClaimOutcome.Expired(),
            _ => InteractionLinkClaimOutcome.Invalid()
        };
    }

    public Task<InteractionLinkStatusResponse?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<InteractionLinkStatusResponse>($"{BasePath}/jobs/{jobId}/status", cancellationToken);

    /// <summary>
    /// The brokered remote-view WebSocket, scoped by the grant cookie to
    /// exactly the job <see cref="ClaimAsync"/> claimed -- built from
    /// <see cref="HttpClient.BaseAddress"/> the same way
    /// <c>ProviderInteractionsApiClient.GetViewWebSocketUri</c> is.
    /// </summary>
    public Uri GetViewWebSocketUri(Guid jobId)
    {
        var baseAddress = httpClient.BaseAddress
            ?? throw new InvalidOperationException("No base address is configured for the API client.");
        return new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme == "https" ? "wss" : "ws",
            Path = $"{BasePath}/jobs/{jobId}/view"
        }.Uri;
    }

    private static async Task<(string Reason, string? ClaimedBy)> ReadReasonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ReasonPayload>(cancellationToken);
            return (payload?.Reason ?? "invalid", payload?.ClaimedBy);
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            return ("invalid", null);
        }
    }

    private sealed record ReasonPayload(string? Reason, string? ClaimedBy);
}

public enum InteractionLinkClaimKind
{
    Claimed,
    ClaimedByOther,
    Done,
    NothingWaiting,
    Expired,
    Invalid
}

public sealed record InteractionLinkClaimOutcome(
    InteractionLinkClaimKind Kind, ClaimInteractionLinkResponse? Response = null, string? ClaimedByDisplayName = null)
{
    public static InteractionLinkClaimOutcome Claimed(ClaimInteractionLinkResponse response) =>
        new(InteractionLinkClaimKind.Claimed, response);

    public static InteractionLinkClaimOutcome ClaimedByOther(string? displayName) =>
        new(InteractionLinkClaimKind.ClaimedByOther, ClaimedByDisplayName: displayName);

    public static InteractionLinkClaimOutcome Done() => new(InteractionLinkClaimKind.Done);

    public static InteractionLinkClaimOutcome NothingWaiting() => new(InteractionLinkClaimKind.NothingWaiting);

    public static InteractionLinkClaimOutcome Expired() => new(InteractionLinkClaimKind.Expired);

    public static InteractionLinkClaimOutcome Invalid() => new(InteractionLinkClaimKind.Invalid);
}
