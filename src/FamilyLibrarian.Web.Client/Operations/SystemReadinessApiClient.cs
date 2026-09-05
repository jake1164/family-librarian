using System.Net.Http.Json;
using FamilyLibrarian.Contracts.Operations;

namespace FamilyLibrarian.Web.Client.Operations;

/// <summary>Typed client for the plain healthy/degraded signal behind the status footer.</summary>
public sealed class SystemReadinessApiClient(HttpClient httpClient)
{
    public Task<SystemReadinessResponse?> GetAsync(CancellationToken cancellationToken = default) =>
        httpClient.GetFromJsonAsync<SystemReadinessResponse>("api/v1/system/readiness", cancellationToken);
}
