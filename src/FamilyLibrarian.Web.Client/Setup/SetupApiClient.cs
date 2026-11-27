using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Setup;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Setup;

/// <summary>Typed client for the current user's first-time setup, and the administrator's setup guidance.</summary>
public sealed class SetupApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string MinePath = "api/v1/me/setup";
    private const string GuidancePath = "api/v1/admin/setup-guidance";

    public Task<UserSetupResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<UserSetupResponse>($"{MinePath}/", cancellationToken);

    public Task<bool> MarkSeenAsync(CancellationToken cancellationToken = default) =>
        PostAsync($"{MinePath}/seen", null, cancellationToken);

    public Task<bool> CompleteAsync(string step, CancellationToken cancellationToken = default) =>
        PostAsync($"{MinePath}/steps/{step}/completed", null, cancellationToken);

    /// <summary>Also dismisses the reminder for these steps.</summary>
    public Task<bool> SkipAsync(IReadOnlyList<string> steps, CancellationToken cancellationToken = default) =>
        PostAsync($"{MinePath}/skipped", JsonContent.Create(new SkipSetupStepsRequest(steps)), cancellationToken);

    public Task<SetupGuidanceSettingsResponse?> GetGuidanceAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<SetupGuidanceSettingsResponse>($"{GuidancePath}/", cancellationToken);

    public Task<SetupGuidanceSaveResult> SetKindleGuidanceAsync(
        SetKindleGuidanceRequest request, CancellationToken cancellationToken = default) =>
        PutGuidanceAsync("kindle", JsonContent.Create(request), cancellationToken);

    public Task<SetupGuidanceSaveResult> SetMessagingGuidanceAsync(
        SetMessagingGuidanceRequest request, CancellationToken cancellationToken = default) =>
        PutGuidanceAsync("messaging", JsonContent.Create(request), cancellationToken);

    public Task<SetupGuidanceSaveResult> SetShowElementHelpAsync(
        bool enabled, CancellationToken cancellationToken = default) =>
        PutGuidanceAsync(
            "messaging/element-help", JsonContent.Create(new SetShowElementHelpRequest(enabled)), cancellationToken);

    private async Task<bool> PostAsync(string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<SetupGuidanceSaveResult> PutGuidanceAsync(
        string section, HttpContent content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{GuidancePath}/{section}") { Content = content };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? new SetupGuidanceSaveResult(
                await response.Content.ReadFromJsonAsync<SetupGuidanceSettingsResponse>(cancellationToken), null)
            : new SetupGuidanceSaveResult(null, await ReadErrorAsync(response, cancellationToken));
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const string fallback = "That change could not be saved.";
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return "These settings are being changed somewhere else. Reload the page and try again.";
        }

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            return problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault() ?? fallback;
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or JsonException)
        {
            return fallback;
        }
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

/// <param name="Settings">Null when the save was refused; <paramref name="Error"/> says why.</param>
public sealed record SetupGuidanceSaveResult(SetupGuidanceSettingsResponse? Settings, string? Error);
