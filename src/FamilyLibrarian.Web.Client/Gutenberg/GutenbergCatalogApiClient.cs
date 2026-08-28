using System.Net.Http.Json;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Gutenberg;

/// <summary>Typed client for administrator controls of the local Project Gutenberg catalogue.</summary>
public sealed class GutenbergCatalogApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string BasePath = "api/v1/admin/gutenberg";

    public Task<GutenbergCatalogStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<GutenbergCatalogStatusResponse>($"{BasePath}/status", cancellationToken);

    public async Task<GutenbergCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/sync");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return new GutenbergCatalogRefreshResult(
                true,
                await response.Content.ReadFromJsonAsync<GutenbergCatalogStatusResponse>(cancellationToken),
                null);
        }

        return new GutenbergCatalogRefreshResult(false, null, "The Project Gutenberg catalogue could not be refreshed.");
    }

    public async Task<GutenbergCatalogPurgeResult> PurgeAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BasePath}/catalog");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<GutenbergCatalogPurgeResponse>(cancellationToken);
            return new GutenbergCatalogPurgeResult(true, result?.DeletedBookCount ?? 0, null);
        }

        return new GutenbergCatalogPurgeResult(false, 0, "The local Project Gutenberg catalogue could not be deleted.");
    }
}

public sealed record GutenbergCatalogStatusResponse(
    bool IsReady,
    DateTimeOffset? LastSuccessfulSyncUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextScheduledSyncUtc,
    int BookCount,
    int FormatCount,
    string Status,
    string? FailureMessage);

public sealed record GutenbergCatalogRefreshResult(
    bool Succeeded,
    GutenbergCatalogStatusResponse? Status,
    string? Error);

public sealed record GutenbergCatalogPurgeResponse(int DeletedBookCount);

public sealed record GutenbergCatalogPurgeResult(bool Succeeded, int DeletedBookCount, string? Error);
