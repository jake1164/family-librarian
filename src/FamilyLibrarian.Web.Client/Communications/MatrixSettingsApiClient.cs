using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Communications;

/// <summary>Typed client for the admin-only Matrix settings routes.</summary>
public sealed class MatrixSettingsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/communications/matrix";

    public Task<MatrixSettingsResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<MatrixSettingsResponse>($"{BasePath}/", cancellationToken);

    public Task<MatrixSettingsResult> SetSettingsAsync(
        SetMatrixSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<MatrixSettingsResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/enabled", new SetMatrixEnabledRequest(enabled), cancellationToken);

    public Task<MatrixSettingsResult> SetAccessTokenAsync(string accessToken, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/access-token", new SetMatrixAccessTokenRequest(accessToken), cancellationToken);

    public Task<MatrixSettingsResult> ClearAccessTokenAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/access-token", null, cancellationToken);

    public async Task<MatrixTestResponse?> SendTestAsync(
        SendMatrixTestRequest request, CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/test")
        {
            Content = JsonContent.Create(request)
        };
        await antiforgery.AttachAsync(httpRequest, cancellationToken);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MatrixTestResponse>(cancellationToken)
            : null;
    }

    private async Task<MatrixSettingsResult> SendAsync<TPayload>(
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
            return new MatrixSettingsResult(true, null,
                await response.Content.ReadFromJsonAsync<MatrixSettingsResponse>(cancellationToken));
        }

        return new MatrixSettingsResult(false, await ReadErrorAsync(response, cancellationToken), null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            return problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault()
                ?? "That change could not be saved.";
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            return "That change could not be saved.";
        }
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record MatrixSettingsResult(bool Succeeded, string? Error, MatrixSettingsResponse? Settings);
