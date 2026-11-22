using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Providers;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Providers;

/// <summary>Typed client for the External Providers admin panel: registered providers.</summary>
public sealed class ExternalProviderApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string ProvidersPath = "api/v1/admin/external-providers";

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

    public Task<ExternalProviderResult> SetProviderRecheckScheduleAsync(
        Guid id, string recheckSchedule, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/recheck-schedule",
            new SetExternalProviderRecheckScheduleRequest(recheckSchedule), cancellationToken);

    public Task<ExternalProviderResult> SetProviderAutomaticAttemptLimitAsync(
        Guid id, int limit, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/automatic-attempt-limit",
            new SetExternalProviderAutomaticAttemptLimitRequest(limit), cancellationToken);

    public Task<ExternalProviderResult> SetProviderConfidenceLevelAsync(
        Guid id, string level, string? ebook, string? audiobook, CancellationToken cancellationToken = default) =>
        SendProviderAsync(HttpMethod.Put, $"{ProvidersPath}/{id}/confidence-level",
            new SetExternalProviderConfidenceLevelRequest(level, ebook, audiobook), cancellationToken);

    public Task<ExternalProviderResult> SetProviderApiKeyAsync(
        Guid id, string apiKey, CancellationToken cancellationToken = default) =>
        SendProviderAsync(
            HttpMethod.Put, $"{ProvidersPath}/{id}/api-key",
            new SetExternalProviderApiKeyRequest(apiKey), cancellationToken);

    public Task<ExternalProviderResult> ClearProviderApiKeyAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendProviderAsync<object>(HttpMethod.Delete, $"{ProvidersPath}/{id}/api-key", null, cancellationToken);

    public Task<ExternalProviderResult> TestProviderAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendProviderAsync<object>(HttpMethod.Post, $"{ProvidersPath}/{id}/test", null, cancellationToken);

    public async Task<bool> RemoveProviderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ProvidersPath}/{id}");
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

