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
