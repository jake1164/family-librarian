using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Publishing;

/// <summary>Typed client for the Audiobookshelf publishing-destination settings.</summary>
public sealed class AudiobookshelfSettingsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/publishing/audiobookshelf";

    public Task<AudiobookshelfSettingsResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<AudiobookshelfSettingsResponse>($"{BasePath}/", cancellationToken);

    public Task<AudiobookshelfResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/enabled", new SetPublishingEnabledRequest(enabled), cancellationToken);

    public Task<AudiobookshelfResult> SetSettingsAsync(
        SetAudiobookshelfSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<AudiobookshelfResult> SetApiTokenAsync(string value, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/api-token", new SetPublishingSecretRequest(value), cancellationToken);

    public Task<AudiobookshelfResult> ClearApiTokenAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/api-token", null, cancellationToken);

    public async Task<PublishingConnectionTestResponse?> TestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/test");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<PublishingConnectionTestResponse>(cancellationToken)
            : null;
    }

    private async Task<AudiobookshelfResult> SendAsync<TPayload>(
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
            return new AudiobookshelfResult(
                true, null, await response.Content.ReadFromJsonAsync<AudiobookshelfSettingsResponse>(cancellationToken));
        }

        return new AudiobookshelfResult(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record AudiobookshelfResult(bool Succeeded, string? Error, AudiobookshelfSettingsResponse? Settings);
