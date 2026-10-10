using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Catalog;

/// <summary>
/// Loads optional source availability after catalog metadata is already visible.
/// Its HTTP client has no independent deadline: cancellation comes from the
/// current browser search or navigation instead.
/// </summary>
public sealed class CatalogAvailabilityApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<CandidateAvailabilityResponse> GetAvailabilityAsync(
        CatalogBookCandidateResponse candidate,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(candidate, "api/v1/catalog/availability");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CandidateAvailabilityResponse>(cancellationToken)
            ?? new CandidateAvailabilityResponse([], []);
    }

    /// <summary>
    /// Checks a raw search candidate against configured availability sources,
    /// without resolving it into a Work first.
    /// </summary>
    public async Task<CandidateAvailabilityRunStartedResponse> StartAsync(
        CatalogBookCandidateResponse candidate,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(candidate, "api/v1/catalog/availability/runs");
        await antiforgery.AttachAsync(request, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CandidateAvailabilityRunStartedResponse>(cancellationToken)
            ?? throw new HttpRequestException("The catalog did not return an availability run.");
    }

    private static HttpRequestMessage CreateRequest(CatalogBookCandidateResponse candidate, string uri)
    {
        var isbn13s = candidate.Editions
            .Select(edition => edition.Isbn13)
            .Where(isbn13 => !string.IsNullOrWhiteSpace(isbn13))
            .Distinct()
            .ToArray();
        return new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new CandidateAvailabilityRequest(candidate.Title, candidate.Authors, isbn13s!))
        };
    }

    public async Task<CandidateAvailabilityRunResponse> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<CandidateAvailabilityRunResponse>(
            $"api/v1/catalog/availability/runs/{runId}", cancellationToken);
        return response ?? throw new HttpRequestException("The catalog did not return availability progress.");
    }

    public async Task CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/catalog/availability/runs/{runId}");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
