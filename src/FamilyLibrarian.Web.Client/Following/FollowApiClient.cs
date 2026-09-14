using System.Net;
using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Following;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Following;

/// <summary>Typed client for the Following (series/author subscription) endpoints.</summary>
public sealed class FollowApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<FollowingSummaryResponse> GetMineAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<FollowingSummaryResponse>(
            "api/v1/me/follows",
            cancellationToken);

        return response ?? new FollowingSummaryResponse([], []);
    }

    public Task<bool> FollowSeriesAsync(Guid seriesId, CancellationToken cancellationToken = default) =>
        PostAsync($"api/v1/me/follows/series/{seriesId}", cancellationToken);

    public Task<bool> UnfollowSeriesAsync(Guid seriesId, CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/v1/me/follows/series/{seriesId}", cancellationToken);

    public Task<bool> FollowAuthorAsync(Guid authorId, CancellationToken cancellationToken = default) =>
        PostAsync($"api/v1/me/follows/authors/{authorId}", cancellationToken);

    public Task<bool> UnfollowAuthorAsync(Guid authorId, CancellationToken cancellationToken = default) =>
        DeleteAsync($"api/v1/me/follows/authors/{authorId}", cancellationToken);

    private async Task<bool> PostAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(method, path, cancellationToken);

        // A token that outlived its identity cookie reads as a bad request. Retry
        // once with a fresh one before showing the user an error they cannot act on.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            antiforgery.Invalidate();
            var retry = await SendOnceAsync(method, path, cancellationToken);
            if (retry.IsSuccessStatusCode)
            {
                response.Dispose();
                return retry;
            }

            retry.Dispose();
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        await antiforgery.AttachAsync(request, cancellationToken);
        return await httpClient.SendAsync(request, cancellationToken);
    }
}
