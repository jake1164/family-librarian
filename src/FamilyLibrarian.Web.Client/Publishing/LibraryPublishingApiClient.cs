using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Publishing;

/// <summary>Typed client for the admin Library Publishing queue.</summary>
public sealed class LibraryPublishingApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    public async Task<PublishingQueueResponse> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetFromJsonAsync<PublishingQueueResponse>(
            "api/v1/admin/publishing/queue", cancellationToken);
        return response ?? new PublishingQueueResponse([], []);
    }

    public Task<bool> RecheckLibraryImportAsync(Guid id, CancellationToken cancellationToken = default) =>
        RecheckAsync($"api/v1/admin/publishing/library-imports/{id}/recheck", cancellationToken);

    public Task<bool> RecheckDeliveryAsync(Guid id, CancellationToken cancellationToken = default) =>
        RecheckAsync($"api/v1/admin/publishing/deliveries/{id}/recheck", cancellationToken);

    private async Task<bool> RecheckAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
