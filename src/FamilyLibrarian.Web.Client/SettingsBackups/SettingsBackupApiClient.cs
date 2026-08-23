using System.Net.Http.Json;
using FamilyLibrarian.Contracts.SettingsBackups;
using FamilyLibrarian.Web.Client.Authentication;
using Microsoft.AspNetCore.Components.Forms;

namespace FamilyLibrarian.Web.Client.SettingsBackups;

/// <summary>Browser client for the administrator-only settings archive flow.</summary>
public sealed class SettingsBackupApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/settings-backup";
    private const long MaxArchiveBytes = 1_048_576;

    public async Task<SettingsBackupDownloadResult> ExportAsync(
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/")
        {
            Content = JsonContent.Create(new CreateSettingsBackupRequest(passphrase))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SettingsBackupDownloadResult(false, await ReadErrorAsync(response, cancellationToken), null, null);
        }

        var filename = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? "family-librarian-settings.family-librarian-settings";
        return new SettingsBackupDownloadResult(
            true,
            null,
            filename.Trim('"'),
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public Task<SettingsBackupPreviewResult> PreviewAsync(
        IBrowserFile archive,
        string passphrase,
        CancellationToken cancellationToken = default) =>
        SendArchiveAsync("preview", archive, passphrase, static async (response, token) =>
            new SettingsBackupPreviewResult(true, null,
                await response.Content.ReadFromJsonAsync<SettingsBackupPreviewResponse>(token)), cancellationToken);

    public Task<SettingsBackupImportResult> ImportAsync(
        IBrowserFile archive,
        string passphrase,
        CancellationToken cancellationToken = default) =>
        SendArchiveAsync("import", archive, passphrase, static async (response, token) =>
            new SettingsBackupImportResult(true, null,
                await response.Content.ReadFromJsonAsync<SettingsBackupImportResponse>(token)), cancellationToken);

    private async Task<T> SendArchiveAsync<T>(
        string operation,
        IBrowserFile archive,
        string passphrase,
        Func<HttpResponseMessage, CancellationToken, Task<T>> success,
        CancellationToken cancellationToken)
    {
        await using var stream = archive.OpenReadStream(MaxArchiveBytes, cancellationToken);
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(archive.ContentType) ? "application/octet-stream" : archive.ContentType);
        form.Add(content, "archive", archive.Name);
        form.Add(new StringContent(passphrase), "passphrase");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/{operation}") { Content = form };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await success(response, cancellationToken);
        }

        var error = await ReadErrorAsync(response, cancellationToken);
        return typeof(T) == typeof(SettingsBackupPreviewResult)
            ? (T)(object)new SettingsBackupPreviewResult(false, error, null)
            : (T)(object)new SettingsBackupImportResult(false, error, null);
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemPayload>(cancellationToken);
            return problem?.Errors?.Values.SelectMany(messages => messages).FirstOrDefault()
                ?? "The settings archive could not be processed.";
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return "The settings archive could not be processed.";
        }
    }

    private sealed record ValidationProblemPayload(Dictionary<string, string[]>? Errors);
}

public sealed record SettingsBackupDownloadResult(bool Succeeded, string? Error, string? Filename, byte[]? Archive);
public sealed record SettingsBackupPreviewResult(bool Succeeded, string? Error, SettingsBackupPreviewResponse? Preview);
public sealed record SettingsBackupImportResult(bool Succeeded, string? Error, SettingsBackupImportResponse? Import);
