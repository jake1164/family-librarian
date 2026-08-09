using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Client.Catalog;

public sealed class CatalogApiClient(HttpClient httpClient)
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
}
