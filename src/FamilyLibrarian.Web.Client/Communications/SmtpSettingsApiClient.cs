using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Communications;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Communications;

/// <summary>Typed client for the admin-only SMTP settings routes.</summary>
public sealed class SmtpSettingsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/communications/smtp";

    public Task<SmtpSettingsResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<SmtpSettingsResponse>($"{BasePath}/", cancellationToken);

    public Task<SmtpSettingsResult> SetSettingsAsync(
        SetSmtpSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<SmtpSettingsResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/enabled", new SetSmtpEnabledRequest(enabled), cancellationToken);

    public Task<SmtpSettingsResult> SetPasswordAsync(string password, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/password", new SetSmtpPasswordRequest(password), cancellationToken);

    public Task<SmtpSettingsResult> ClearPasswordAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/password", null, cancellationToken);

    public async Task<SmtpTestResponse?> SendTestAsync(string recipientAddress, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/test")
        {
            Content = JsonContent.Create(new SendSmtpTestRequest(recipientAddress))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SmtpTestResponse>(cancellationToken)
            : null;
    }

    private async Task<SmtpSettingsResult> SendAsync<TPayload>(
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
            return new SmtpSettingsResult(true, null,
                await response.Content.ReadFromJsonAsync<SmtpSettingsResponse>(cancellationToken));
        }

        return new SmtpSettingsResult(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record SmtpSettingsResult(bool Succeeded, string? Error, SmtpSettingsResponse? Settings);
