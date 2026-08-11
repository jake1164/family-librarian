using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Integrations;

namespace FamilyLibrarian.Web.Client.Integrations;

/// <summary>
/// Typed client for the Admin Metadata Integrations endpoints.
/// </summary>
/// <remarks>
/// Credentials travel one way only. <see cref="SetCredentialAsync"/> sends a key
/// to the host; nothing here can read one back, because the API does not return
/// stored credential values.
/// </remarks>
public sealed class MetadataIntegrationsApiClient(HttpClient httpClient)
{
    private const string BasePath = "api/v1/admin/integrations/metadata";
    private const string AntiforgeryHeaderName = "X-CSRF-TOKEN";

    private string? _antiforgeryToken;

    public async Task<IReadOnlyList<MetadataProviderStatusResponse>> GetProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<MetadataProviderListResponse>(
            $"{BasePath}/",
            cancellationToken);

        return response?.Providers ?? [];
    }

    public Task<MetadataIntegrationsResult> SetEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Put,
            $"{BasePath}/{Uri.EscapeDataString(providerId)}/enabled",
            new SetMetadataProviderEnabledRequest(enabled),
            cancellationToken);

    public Task<MetadataIntegrationsResult> SetCredentialAsync(
        string providerId,
        string credential,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Put,
            $"{BasePath}/{Uri.EscapeDataString(providerId)}/credential",
            new SetMetadataProviderCredentialRequest(credential),
            cancellationToken);

    public Task<MetadataIntegrationsResult> ClearCredentialAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        SendAsync<object>(
            HttpMethod.Delete,
            $"{BasePath}/{Uri.EscapeDataString(providerId)}/credential",
            payload: null,
            cancellationToken);

    public async Task<MetadataProviderTestResponse?> TestAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync<object>(
            HttpMethod.Post,
            $"{BasePath}/{Uri.EscapeDataString(providerId)}/test",
            payload: null,
            cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MetadataProviderTestResponse>(cancellationToken)
            : null;
    }

    /// <summary>
    /// Fetches the anti-forgery request token, caching it for the page's lifetime.
    /// </summary>
    /// <remarks>
    /// The paired cookie is HttpOnly, so the token must be requested from the
    /// host rather than read out of <c>document.cookie</c>.
    /// </remarks>
    private async Task<string?> GetAntiforgeryTokenAsync(CancellationToken cancellationToken)
    {
        if (_antiforgeryToken is not null)
        {
            return _antiforgeryToken;
        }

        var response = await httpClient.GetFromJsonAsync<AntiforgeryTokenPayload>(
            "api/v1/antiforgery/token",
            cancellationToken);

        _antiforgeryToken = response?.Token;
        return _antiforgeryToken;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload? payload,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var request = new HttpRequestMessage(method, path);

        var token = await GetAntiforgeryTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add(AntiforgeryHeaderName, token);
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        return request;
    }

    private Task<MetadataIntegrationsResult> SendAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload? payload,
        CancellationToken cancellationToken)
        where TPayload : class =>
        SendAsync(async () =>
        {
            using var request = await CreateRequestAsync(method, path, payload, cancellationToken);
            return await httpClient.SendAsync(request, cancellationToken);
        });

    private static async Task<MetadataIntegrationsResult> SendAsync(
        Func<Task<HttpResponseMessage>> send)
    {
        using var response = await send();

        if (response.IsSuccessStatusCode)
        {
            return new MetadataIntegrationsResult(
                true,
                null,
                await response.Content.ReadFromJsonAsync<MetadataProviderStatusResponse>());
        }

        // The host answers a rejected change with a validation problem carrying a
        // human-readable reason (for example, an externally managed credential).
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>();
            var message = problem?.Errors?.Values
                .SelectMany(messages => messages)
                .FirstOrDefault();

            return new MetadataIntegrationsResult(false, message ?? "That change was rejected.", null);
        }

        return new MetadataIntegrationsResult(false, "That change could not be saved.", null);
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);

    private sealed record AntiforgeryTokenPayload(string Token);
}

public sealed record MetadataIntegrationsResult(
    bool Succeeded,
    string? Error,
    MetadataProviderStatusResponse? Provider);
