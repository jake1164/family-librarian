using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Providers;

/// <summary>Typed client for the External Providers admin panel: registered providers, the private-egress gateway, and repository catalogs.</summary>
public sealed class ExternalProviderApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string ProvidersPath = "api/v1/admin/external-providers";
    private const string GatewayPath = "api/v1/admin/private-egress-gateway";
    private const string CatalogsPath = "api/v1/admin/provider-catalogs";

    // --- External providers -------------------------------------------------

    public async Task<IReadOnlyList<ExternalProviderResponse>> GetProvidersAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<ExternalProviderResponse[]>($"{ProvidersPath}/", cancellationToken);
        return response ?? [];
    }

    public Task<ExternalProviderResult> CreateProviderAsync(
        string providerId, string displayName, string baseUrl, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Post, $"{ProvidersPath}/",
            new CreateExternalProviderRequest(providerId, displayName, baseUrl), cancellationToken);

    public Task<ExternalProviderResult> SetProviderDetailsAsync(
        Guid id, string displayName, string baseUrl, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/details",
            new SetExternalProviderDetailsRequest(displayName, baseUrl), cancellationToken);

    public Task<ExternalProviderResult> SetProviderEnabledAsync(
        Guid id, bool enabled, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/enabled",
            new SetExternalProviderEnabledRequest(enabled), cancellationToken);

    public Task<ExternalProviderResult> SetProviderApiKeyAsync(
        Guid id, string apiKey, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/api-key",
            new SetExternalProviderApiKeyRequest(apiKey), cancellationToken);

    public Task<ExternalProviderResult> ClearProviderApiKeyAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendProviderAsync<object>(HttpMethod.Delete, $"{ProvidersPath}/{id}/api-key", null, cancellationToken);

    public Task<ExternalProviderResult> TestProviderAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendProviderAsync<object>(HttpMethod.Post, $"{ProvidersPath}/{id}/test", null, cancellationToken);

    public Task<ExternalProviderResult> SetProviderEgressPolicyOverrideAsync(
        Guid id, string? egressPolicy, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/egress-policy-override",
            new SetExternalProviderEgressPolicyOverrideRequest(egressPolicy), cancellationToken);

    public async Task<bool> RemoveProviderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ProvidersPath}/{id}");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // --- Private egress gateway ----------------------------------------------

    public Task<PrivateEgressGatewayResponse?> GetGatewayAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<PrivateEgressGatewayResponse>($"{GatewayPath}/", cancellationToken);

    public Task<GatewayResult> SetGatewayEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendGatewayAsync(
            HttpMethod.Put, $"{GatewayPath}/enabled", new SetPrivateEgressGatewayEnabledRequest(enabled), cancellationToken);

    public Task<GatewayResult> SetGatewayEndpointAsync(
        string? gatewayEndpoint, CancellationToken cancellationToken = default) =>
        SendGatewayAsync(
            HttpMethod.Put, $"{GatewayPath}/endpoint", new SetPrivateEgressGatewayEndpointRequest(gatewayEndpoint),
            cancellationToken);

    public Task<GatewayResult> TestGatewayAsync(CancellationToken cancellationToken = default) =>
        SendGatewayAsync<object>(HttpMethod.Post, $"{GatewayPath}/test", null, cancellationToken);

    // --- Provider catalogs -----------------------------------------------------

    public async Task<IReadOnlyList<ProviderCatalogResponse>> GetCatalogsAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<ProviderCatalogResponse[]>($"{CatalogsPath}/", cancellationToken);
        return response ?? [];
    }

    public async Task<bool> AddCatalogAsync(string url, string? displayName, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CatalogsPath}/")
        {
            Content = JsonContent.Create(new AddProviderCatalogRequest(url, displayName))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SetCatalogEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{CatalogsPath}/{id}/enabled")
        {
            Content = JsonContent.Create(new SetProviderCatalogEnabledRequest(enabled))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RefreshCatalogAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{CatalogsPath}/{id}/refresh");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveCatalogAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{CatalogsPath}/{id}");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    // --- Shared send helpers ---------------------------------------------------

    private async Task<ExternalProviderResult> SendProviderAsync<TPayload>(
        HttpMethod method, string path, TPayload? payload, CancellationToken cancellationToken)
        where TPayload : class
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new ExternalProviderResult(
                true, null, await response.Content.ReadFromJsonAsync<ExternalProviderResponse>(cancellationToken));
        }

        return new ExternalProviderResult(false, await ReadErrorAsync(response, cancellationToken), null);
    }

    private async Task<GatewayResult> SendGatewayAsync<TPayload>(
        HttpMethod method, string path, TPayload? payload, CancellationToken cancellationToken)
        where TPayload : class
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new GatewayResult(
                true, null, await response.Content.ReadFromJsonAsync<PrivateEgressGatewayResponse>(cancellationToken));
        }

        return new GatewayResult(false, await ReadErrorAsync(response, cancellationToken), null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            var message = problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            // Fall through to the generic message below.
        }

        return "That change could not be saved.";
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record ExternalProviderResult(bool Succeeded, string? Error, ExternalProviderResponse? Provider);

public sealed record GatewayResult(bool Succeeded, string? Error, PrivateEgressGatewayResponse? Settings);
