using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Authentication;

namespace FamilyLibrarian.Web.Client.Authentication;

/// <summary>Typed client for the OIDC settings panel and the public sign-in status probe.</summary>
public sealed class OidcSettingsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/authentication/oidc";

    /// <summary>Anonymous-reachable: lets the login page decide whether to show an OIDC button.</summary>
    public Task<OidcSignInStatusResponse?> GetSignInStatusAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<OidcSignInStatusResponse>("api/auth/oidc/status", cancellationToken);

    public Task<OidcSettingsResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<OidcSettingsResponse>($"{BasePath}/", cancellationToken);

    public Task<OidcResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/enabled", new SetOidcEnabledRequest(enabled), cancellationToken);

    public Task<OidcResult> SetSettingsAsync(SetOidcSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<OidcResult> SetClientSecretAsync(string clientSecret, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/client-secret", new SetOidcClientSecretRequest(clientSecret), cancellationToken);

    public Task<OidcResult> ClearClientSecretAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/client-secret", null, cancellationToken);

    public Task<OidcResult> SetLocalLoginDisabledAsync(bool disabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/local-login-disabled", new SetOidcLocalLoginDisabledRequest(disabled), cancellationToken);

    public async Task<OidcConnectionTestResponse?> TestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/test");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<OidcConnectionTestResponse>(cancellationToken)
            : null;
    }

    private async Task<OidcResult> SendAsync<TPayload>(
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
            return new OidcResult(true, null, await response.Content.ReadFromJsonAsync<OidcSettingsResponse>(cancellationToken));
        }

        return new OidcResult(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record OidcResult(bool Succeeded, string? Error, OidcSettingsResponse? Settings);
