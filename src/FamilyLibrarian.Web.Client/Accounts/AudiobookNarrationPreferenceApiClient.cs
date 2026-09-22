using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Accounts;

/// <summary>Typed client for a household member's own audiobook narration preference.</summary>
public sealed class AudiobookNarrationPreferenceApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/me/audiobook-narration-preference";

    public Task<AudiobookNarrationPreferenceResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<AudiobookNarrationPreferenceResponse>($"{BasePath}/", cancellationToken);

    public async Task<AudiobookNarrationPreferenceResult> SetAsync(
        string preference, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/")
        {
            Content = JsonContent.Create(new SetAudiobookNarrationPreferenceRequest(preference))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new AudiobookNarrationPreferenceResult(
                true, null, await response.Content.ReadFromJsonAsync<AudiobookNarrationPreferenceResponse>(cancellationToken));
        }

        return new AudiobookNarrationPreferenceResult(false, await ReadErrorAsync(response, cancellationToken), null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            return problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault()
                ?? "That could not be saved.";
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException
            or System.Text.Json.JsonException)
        {
            return "That could not be saved.";
        }
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record AudiobookNarrationPreferenceResult(bool Succeeded, string? Error, AudiobookNarrationPreferenceResponse? Status);
