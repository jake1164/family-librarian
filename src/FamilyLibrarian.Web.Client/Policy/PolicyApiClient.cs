using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Policy;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Policy;

/// <summary>Typed client for the acquisition-policy admin settings.</summary>
public sealed class PolicyApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/policy";

    public async Task<IReadOnlyList<PolicyProfileResponse>> GetProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<PolicyProfileResponse[]>(
            $"{BasePath}/profiles", cancellationToken);
        return response ?? [];
    }

    public Task<AcquisitionPolicySettingsResponse?> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<AcquisitionPolicySettingsResponse>($"{BasePath}/settings", cancellationToken);

    public async Task<PolicyResult> SetDefaultProfileAsync(
        string profileId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/settings")
        {
            Content = JsonContent.Create(new SetDefaultPolicyProfileRequest(profileId))
        };
        await antiforgery.AttachAsync(request, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new PolicyResult(
                true, null, await response.Content.ReadFromJsonAsync<AcquisitionPolicySettingsResponse>(cancellationToken));
        }

        return new PolicyResult(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record PolicyResult(bool Succeeded, string? Error, AcquisitionPolicySettingsResponse? Settings);
