using System.Text.Json.Nodes;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Providers;

/// <summary>
/// Fetches and re-serializes a repository catalog's entries array. No
/// signature verification — see the M13 plan's scope note; this is purely a
/// discovery listing the admin explicitly opted into.
/// </summary>
public sealed class ProviderCatalogFetcher(IHttpClientFactory httpClientFactory) : IProviderCatalogFetcher
{
    // A catalog listing is a short JSON array of provider entries — this is
    // generous headroom over any legitimate one, not a working ceiling. The
    // default is 2 GB, buffered entirely in memory before this method ever
    // sees it; an admin-supplied catalog URL is exactly the kind of external
    // input F9 in the architecture review flagged as unbounded. Exceeding it
    // surfaces as HttpRequestException, already caught below.
    private const long MaxCatalogResponseBytes = 10 * 1024 * 1024;

    public async Task<ProviderCatalogFetchOutcome> FetchAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = MaxCatalogResponseBytes;

            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ProviderCatalogFetchOutcome.Failure(
                    $"The catalog responded with status {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = JsonNode.Parse(body);
            var entries = document is JsonObject documentObject && documentObject["providers"] is JsonArray array
                ? array
                : document as JsonArray;

            if (entries is null)
            {
                return ProviderCatalogFetchOutcome.Failure(
                    "The catalog was not a JSON array or an object with a \"providers\" array.");
            }

            return ProviderCatalogFetchOutcome.Success(entries.ToJsonString());
        }
        catch (HttpRequestException exception)
        {
            return ProviderCatalogFetchOutcome.Failure($"The catalog is unreachable: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderCatalogFetchOutcome.Failure("The catalog did not respond in time.");
        }
        catch (System.Text.Json.JsonException)
        {
            return ProviderCatalogFetchOutcome.Failure("The catalog was not valid JSON.");
        }
    }
}
