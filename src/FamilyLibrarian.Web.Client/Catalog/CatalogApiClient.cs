using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Catalog;

public sealed class CatalogApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<CatalogSearchResponse> SearchAsync(
        string searchText,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<CatalogSearchResponse>(
            $"api/v1/catalog/search?q={Uri.EscapeDataString(searchText)}",
            cancellationToken);

        return response ?? new CatalogSearchResponse([], []);
    }

    public Task<CatalogBookCandidateResponse?> GetCandidateAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<CatalogBookCandidateResponse>(
            $"api/v1/catalog/candidates/{Uri.EscapeDataString(providerId)}/{Uri.EscapeDataString(externalId)}",
            cancellationToken);

    /// <summary>
    /// Turns a provider candidate into a canonical catalog Work.
    /// </summary>
    /// <remarks>
    /// This writes to the catalog, so it carries an anti-forgery token like every
    /// other state-changing call.
    /// </remarks>
    public async Task<CatalogWorkResponse> ResolveCandidateAsync(
        string providerId,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/catalog/candidates/{Uri.EscapeDataString(providerId)}/{Uri.EscapeDataString(externalId)}/resolve");
        await antiforgery.AttachAsync(request, cancellationToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CatalogWorkResponse>(cancellationToken)
            ?? throw new HttpRequestException("The catalog did not return a resolved Work.");
    }

    public Task<CatalogWorkResponse?> GetWorkAsync(
        Guid workId,
        CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<CatalogWorkResponse>(
            $"api/v1/catalog/works/{workId}",
            cancellationToken);

    public async Task<WorkFulfillmentOptionsResponse> GetFulfillmentOptionsAsync(
        Guid workId,
        CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<WorkFulfillmentOptionsResponse>(
            $"api/v1/catalog/works/{workId}/fulfillment-options",
            cancellationToken);

        return response ?? new WorkFulfillmentOptionsResponse([], []);
    }
}
