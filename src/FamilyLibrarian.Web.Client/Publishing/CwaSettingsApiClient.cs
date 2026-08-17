using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Publishing;

/// <summary>Typed client for the CWA publishing-destination settings.</summary>
public sealed class CwaSettingsApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/publishing/cwa";

    public Task<CwaSettingsResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<CwaSettingsResponse>($"{BasePath}/", cancellationToken);

    public Task<CwaResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/enabled", new SetPublishingEnabledRequest(enabled), cancellationToken);

    public Task<CwaResult> SetSettingsAsync(SetCwaSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/", request, cancellationToken);

    public Task<CwaResult> SetSftpPrivateKeyAsync(string value, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/sftp-key", new SetPublishingSecretRequest(value), cancellationToken);

    public Task<CwaResult> ClearSftpPrivateKeyAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/sftp-key", null, cancellationToken);

    public Task<CwaResult> SetSftpPassphraseAsync(string value, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/sftp-passphrase", new SetPublishingSecretRequest(value), cancellationToken);

    public Task<CwaResult> ClearSftpPassphraseAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/sftp-passphrase", null, cancellationToken);

    public Task<CwaResult> SetSftpPasswordAsync(string value, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/sftp-password", new SetPublishingSecretRequest(value), cancellationToken);

    public Task<CwaResult> ClearSftpPasswordAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/sftp-password", null, cancellationToken);

    public Task<CwaResult> TrustSftpHostKeyAsync(string fingerprint, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/sftp-host-key", new TrustSftpHostKeyRequest(fingerprint), cancellationToken);

    public Task<CwaResult> SetOpdsPasswordAsync(string value, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"{BasePath}/opds-password", new SetPublishingSecretRequest(value), cancellationToken);

    public Task<CwaResult> ClearOpdsPasswordAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"{BasePath}/opds-password", null, cancellationToken);

    public Task<PublishingConnectionTestResponse?> TestIngestAsync(
        TestCwaIngestRequest request,
        CancellationToken cancellationToken = default) =>
        TestAsync("test-ingest", request, cancellationToken);

    public Task<PublishingConnectionTestResponse?> TestOpdsAsync(
        TestCwaOpdsRequest request,
        CancellationToken cancellationToken = default) =>
        TestAsync("test-opds", request, cancellationToken);

    private async Task<PublishingConnectionTestResponse?> TestAsync(
        string operation,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/{operation}");
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<PublishingConnectionTestResponse>(cancellationToken)
            : null;
    }

    private async Task<CwaResult> SendAsync<TPayload>(
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
            return new CwaResult(true, null, await response.Content.ReadFromJsonAsync<CwaSettingsResponse>(cancellationToken));
        }

        return new CwaResult(false, await ReadErrorAsync(response, cancellationToken), null);
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

public sealed record CwaResult(bool Succeeded, string? Error, CwaSettingsResponse? Settings);
