using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Accounts;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Accounts;

/// <summary>Typed client for the current user's default request format.</summary>
public sealed class DefaultRequestFormatApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string Path = "api/v1/me/default-request-format/";

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default) =>
        (await httpClient.GetFromJsonAsync<DefaultRequestFormatResponse>(Path, cancellationToken))?.Format;

    public async Task<bool> SetAsync(string format, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path)
        {
            Content = JsonContent.Create(new SetDefaultRequestFormatRequest(format))
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
