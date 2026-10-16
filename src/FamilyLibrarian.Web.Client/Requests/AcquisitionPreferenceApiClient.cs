using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Requests;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Requests;

public sealed class AcquisitionPreferenceApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string Path = "api/v1/me/acquisition-preferences/";
    public Task<AcquisitionPreferenceResponse[]?> ListAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<AcquisitionPreferenceResponse[]>(Path, cancellationToken);

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path + id);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
