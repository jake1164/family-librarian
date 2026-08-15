using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Integrations;

/// <summary>
/// Typed client for the Admin Metadata Integrations endpoints.
/// </summary>
/// <remarks>
/// Credentials travel one way only. <see cref="SetCredentialAsync"/> sends a key
/// to the host; nothing here can read one back, because the API does not return
/// stored credential values.
/// </remarks>
public sealed class MetadataIntegrationsApiClient(
    HttpClient httpClient,
    AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/integrations/metadata";

    public async Task<IReadOnlyList<ProviderStatusResponse>> GetProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<ProviderListResponse>(
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
            new SetProviderEnabledRequest(enabled),
            cancellationToken);

    public Task<MetadataIntegrationsResult> SetCredentialAsync(
        string providerId,
        string credential,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Put,
            $"{BasePath}/{Uri.EscapeDataString(providerId)}/credential",
            new SetProviderCredentialRequest(credential),
            cancellationToken);

    public Task<MetadataIntegrationsResult> ClearCredentialAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        SendAsync<object>(
            HttpMethod.Delete,
            $"{BasePath}/{Uri.EscapeDataString(providerId)}/credential",
            payload: null,
            cancellationToken);

    public async Task<ProviderTestResponse?> TestAsync(
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
            ? await response.Content.ReadFromJsonAsync<ProviderTestResponse>(cancellationToken)
            : null;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync<TPayload>(
        HttpMethod method,
        string path,
        TPayload? payload,
        CancellationToken cancellationToken)
        where TPayload : class
    {
        var request = new HttpRequestMessage(method, path);
        await antiforgery.AttachAsync(request, cancellationToken);

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
                await response.Content.ReadFromJsonAsync<ProviderStatusResponse>());
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
}

public sealed record MetadataIntegrationsResult(
    bool Succeeded,
    string? Error,
    ProviderStatusResponse? Provider);
