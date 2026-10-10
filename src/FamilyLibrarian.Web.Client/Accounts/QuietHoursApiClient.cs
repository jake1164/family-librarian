using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Accounts;

/// <summary>Typed client for a household member's own quiet-hours window (HUMAN-ACQ-1 D9).</summary>
public sealed class QuietHoursApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/me/quiet-hours";

    public Task<QuietHoursResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<QuietHoursResponse>($"{BasePath}/", cancellationToken);

    /// <summary>All three null clears the window.</summary>
    public async Task<QuietHoursResult> SetAsync(
        string? timeZoneId, int? startMinute, int? endMinute, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/")
        {
            Content = JsonContent.Create(new SetQuietHoursRequest(timeZoneId, startMinute, endMinute))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new QuietHoursResult(true, null, await response.Content.ReadFromJsonAsync<QuietHoursResponse>(cancellationToken));
        }

        return new QuietHoursResult(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record QuietHoursResult(bool Succeeded, string? Error, QuietHoursResponse? Status);
