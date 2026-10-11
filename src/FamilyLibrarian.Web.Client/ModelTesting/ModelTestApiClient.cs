using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.ModelTesting;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.ModelTesting;

/// <summary>Typed client for the administrator-only model test. It holds no credential: the connection is the shared one on the AI models page.</summary>
public sealed class ModelTestApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/model-test";

    public async Task<ModelTestSettingsResponse> GetAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<ModelTestSettingsResponse>($"{BasePath}/", cancellationToken)
        ?? throw new InvalidOperationException("The host returned no settings.");

    public Task<ModelTestCallResult<ModelTestSettingsResponse>> SaveAsync(SaveModelTestSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ModelTestSettingsResponse>(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<ModelTestCallResult<ModelTestSettingsResponse>> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync<ModelTestSettingsResponse>(HttpMethod.Put, $"{BasePath}/enabled", new SetModelTestEnabledRequest(enabled), cancellationToken);

    public Task<ModelTestCallResult<ModelTestReportResponse>> RunAsync(RunModelTestRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ModelTestReportResponse>(HttpMethod.Post, $"{BasePath}/run", request, cancellationToken);

    private async Task<ModelTestCallResult<T>> SendAsync<T>(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
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
            return new ModelTestCallResult<T>(true, null, await response.Content.ReadFromJsonAsync<T>(cancellationToken));
        }

        return new ModelTestCallResult<T>(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record ModelTestCallResult<T>(bool Succeeded, string? Error, T? Value)
    where T : class;
