using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Acquisition;

/// <summary>Typed, administrator-only client for provider human-interaction control.</summary>
public sealed class ProviderInteractionsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/requests/provider-interactions";

    public async Task<IReadOnlyList<ProviderInteractionResponse>> GetAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ProviderInteractionResponse[]>(BasePath, cancellationToken) ?? [];

    public Task<ProviderInteractionCommandOutcome> StartAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        SendAsync(jobId, "start", cancellationToken);

    public Task<ProviderInteractionCommandOutcome> UseFallbackAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        SendAsync(jobId, "fallback", cancellationToken);

    public Task<ProviderInteractionCommandOutcome> CancelAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        SendAsync(jobId, "cancel", cancellationToken);

    /// <summary>Explicit, audited override: replaces whoever currently holds the claim, then starts the session.</summary>
    public Task<ProviderInteractionCommandOutcome> TakeOverAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        SendAsync(jobId, "take-over", cancellationToken);

    /// <summary>
    /// The brokered remote-view WebSocket (HUMAN-ACQ-1 Phase 3). Built from
    /// <see cref="HttpClient.BaseAddress"/> rather than a relative path,
    /// since a WebSocket connection needs an absolute <c>ws(s)://</c> URI --
    /// there is no cookie/header injection here to replicate: the browser
    /// attaches the same-origin session cookie to the upgrade request
    /// automatically, the same way it does for this client's own HTTP calls.
    /// </summary>
    public Uri GetViewWebSocketUri(Guid jobId)
    {
        var baseAddress = httpClient.BaseAddress
            ?? throw new InvalidOperationException("No base address is configured for the API client.");
        return new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme == "https" ? "wss" : "ws",
            Path = $"{BasePath}/{jobId}/view"
        }.Uri;
    }

    private async Task<ProviderInteractionCommandOutcome> SendAsync(
        Guid jobId, string action, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/{jobId}/{action}");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? new ProviderInteractionCommandOutcome(true, null)
            : new ProviderInteractionCommandOutcome(false, await ReadErrorAsync(response, cancellationToken));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ErrorPayload>(cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Message))
            {
                return payload.Message;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            // Fall through to a safe generic message for non-application responses.
        }

        return "The provider interaction could not be updated. Please reload and try again.";
    }

    private sealed record ErrorPayload(string? Message);
}

public sealed record ProviderInteractionCommandOutcome(bool Succeeded, string? Error);
