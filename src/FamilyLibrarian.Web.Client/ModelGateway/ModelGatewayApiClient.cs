using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.ModelGateway;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.ModelGateway;

/// <summary>Typed client for the administrator-only model test. The API key is write-only: nothing here can read it back.</summary>
public sealed class ModelGatewayApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/model-gateway";

    public async Task<ModelGatewaySettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ModelGatewaySettingsResponse>($"{BasePath}/", cancellationToken)
        ?? throw new InvalidOperationException("The host returned no settings.");

    public async Task<ModelUsageResponse> GetUsageAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ModelUsageResponse>($"{BasePath}/usage", cancellationToken)
        ?? throw new InvalidOperationException("The host returned no usage.");

    public Task<ModelGatewayCallResult<ModelGatewaySettingsResponse>> SaveAsync(SaveModelGatewaySettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ModelGatewaySettingsResponse>(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<ModelGatewayCallResult<ModelGatewaySettingsResponse>> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync<ModelGatewaySettingsResponse>(HttpMethod.Put, $"{BasePath}/enabled", new SetModelGatewayEnabledRequest(enabled), cancellationToken);

    public Task<ModelGatewayCallResult<ModelGatewaySettingsResponse>> SetApiKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
        SendAsync<ModelGatewaySettingsResponse>(HttpMethod.Put, $"{BasePath}/api-key", new SetModelGatewayApiKeyRequest(apiKey), cancellationToken);

    public Task<ModelGatewayCallResult<ModelGatewaySettingsResponse>> ClearApiKeyAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ModelGatewaySettingsResponse>(HttpMethod.Delete, $"{BasePath}/api-key", null, cancellationToken);

    private async Task<ModelGatewayCallResult<T>> SendAsync<T>(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
        where T : class
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
            return new ModelGatewayCallResult<T>(true, null, await response.Content.ReadFromJsonAsync<T>(cancellationToken));
        }

        return new ModelGatewayCallResult<T>(false, await ReadErrorAsync(response, cancellationToken), null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return "Administrator access is required.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(cancellationToken);
            return problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault() ?? problem?.Detail ?? "That could not be done.";
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return "That could not be done.";
        }
    }

    private sealed record ProblemPayload(Dictionary<string, string[]>? Errors, string? Detail);
}

public sealed record ModelGatewayCallResult<T>(bool Succeeded, string? Error, T? Value)
    where T : class;

