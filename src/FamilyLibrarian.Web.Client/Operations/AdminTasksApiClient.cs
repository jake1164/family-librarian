using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Operations;

namespace FamilyLibrarian.Web.Client.Operations;

/// <summary>Typed read-only client for the administrator task dashboard.</summary>
public sealed class AdminTasksApiClient(HttpClient httpClient)
{
    public Task<AdminTasksResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<AdminTasksResponse>("api/v1/admin/tasks/", cancellationToken);
}
