using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Integrations;
using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Infrastructure.Publishing;

/// <summary>
/// A thin client over Audiobookshelf's <c>/api/upload</c> and library-items
/// endpoints, confirmed real (not folder-scan-only) via the public API
/// reference and a production third-party integration (Libation) that uses
/// the same endpoint.
/// </summary>
/// <remarks>
/// Response parsing is deliberately defensive (<see cref="JsonNode"/> rather
/// than a strict DTO): the exact field names for a library item's title and
/// author were not independently verified against a live instance while this
/// was written, so a shape mismatch degrades to "not found" rather than an
/// exception — the same "not found is a normal outcome" posture used
/// throughout this pipeline. Revisit against a real Audiobookshelf instance
/// if matching proves unreliable in practice.
/// </remarks>
public sealed class AudiobookshelfApiClient(
    IHttpClientFactory httpClientFactory,
    IAudiobookshelfSettingsStore settingsStore,
    ICredentialProtector protector) : IAudiobookshelfApiClient
{
    public async Task<string?> FindExistingItemIdAsync(
        string title, string? author, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.LibraryId) || !settings.HasApiToken)
        {
            return null;
        }

        var token = await ResolveTokenAsync(settings.ProtectedApiToken!, settings.ApiTokenFormatVersion);
        if (token is null)
        {
            return null;
        }

        using var client = CreateClient(token);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{settings.BaseUrl.TrimEnd('/')}/api/libraries/{settings.LibraryId}/items");

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return FindMatchingItemId(body, title, author);
    }

    public Task<AudiobookshelfUploadResult> UploadAsync(
        Stream content,
        string filename,
        string title,
        string? author,
        CancellationToken cancellationToken) =>
        UploadAsync([(content, filename)], title, author, cancellationToken);

    public Task<AudiobookshelfUploadResult> UploadBundleAsync(
        IReadOnlyList<(Stream Content, string Filename)> tracks,
        string title,
        string? author,
        CancellationToken cancellationToken) =>
        UploadAsync(tracks, title, author, cancellationToken);

    private async Task<AudiobookshelfUploadResult> UploadAsync(
        IReadOnlyList<(Stream Content, string Filename)> tracks,
        string title,
        string? author,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.FindAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl) ||
            string.IsNullOrWhiteSpace(settings.LibraryId) || string.IsNullOrWhiteSpace(settings.FolderId) ||
            !settings.HasApiToken)
        {
            return new AudiobookshelfUploadResult(false, null, "Audiobookshelf is not fully configured.");
        }

        var token = await ResolveTokenAsync(settings.ProtectedApiToken!, settings.ApiTokenFormatVersion);
        if (token is null)
        {
            return new AudiobookshelfUploadResult(false, null, "The stored Audiobookshelf API token could not be decrypted.");
        }

        using var client = CreateClient(token);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(settings.LibraryId), "library" },
            { new StringContent(settings.FolderId), "folder" },
            { new StringContent(title), "title" }
        };

        if (!string.IsNullOrWhiteSpace(author))
        {
            form.Add(new StringContent(author), "author");
        }

        // Every track is added under the same "files" field name — the
        // upload endpoint accepts repeated parts for a multi-file item, the
        // same way a single-file upload always has.
        foreach (var (content, filename) in tracks)
        {
            var fileContent = new StreamContent(content);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "files", filename);
        }

        using var response = await client.PostAsync($"{settings.BaseUrl.TrimEnd('/')}/api/upload", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new AudiobookshelfUploadResult(false, null, $"Upload failed with status {(int)response.StatusCode}.");
        }

        return new AudiobookshelfUploadResult(true, ExtractItemId(body), null);
    }

    private async Task<string?> ResolveTokenAsync(string protectedValue, int formatVersion) =>
        await Task.FromResult(
            protector.Unprotect(PublishingSecretPurposes.AudiobookshelfApiToken, protectedValue, formatVersion));

    private HttpClient CreateClient(string token)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string? FindMatchingItemId(string listResponseJson, string title, string? author)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(listResponseJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        var items = root?["results"]?.AsArray() ?? root?["items"]?.AsArray();
        if (items is null)
        {
            return null;
        }

        foreach (var item in items)
        {
            var metadata = item?["media"]?["metadata"];
            var itemTitle = metadata?["title"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(itemTitle) ||
                !itemTitle.Contains(title, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(author))
            {
                var itemAuthor = metadata?["authorName"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(itemAuthor) &&
                    !itemAuthor.Contains(author, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            var id = item?["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    private static string? ExtractItemId(string uploadResponseJson)
    {
        try
        {
            var root = JsonNode.Parse(uploadResponseJson);
            return root?["id"]?.GetValue<string>() ?? root?["libraryItem"]?["id"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
