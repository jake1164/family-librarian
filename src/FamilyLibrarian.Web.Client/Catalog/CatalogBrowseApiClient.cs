using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Client.Catalog;

public enum CatalogBrowseOutcome
{
    Ok,

    /// <summary>A saved id that does not exist, or a temporary context that expired, was evicted or is not this user's.</summary>
    NotFound,

    /// <summary>The host rejected the page number or reference. The pages only send validated values, so this is unexpected.</summary>
    InvalidRequest,

    /// <summary>The session ended. The page sends the user to sign in rather than showing a data error.</summary>
    Unauthorized,

    /// <summary>Network failure, server error or an unreadable body. Never carries exception text to the page.</summary>
    Unavailable
}

public sealed record CatalogBrowseResult<T>(CatalogBrowseOutcome Outcome, T? Value = null) where T : class
{
    public bool IsOk => Outcome == CatalogBrowseOutcome.Ok && Value is not null;
}

/// <summary>
/// Typed client for the read-only <c>/api/v1/catalog/browse</c> routes. Every call is a GET: it follows, requests,
/// resolves and imports nothing, and the host answers it without contacting a metadata provider.
/// </summary>
public sealed class CatalogBrowseApiClient(HttpClient httpClient)
{
    private const string Root = "api/v1/catalog/browse";

    public Task<CatalogBrowseResult<CatalogAuthorBrowseResponse>> GetAuthorAsync(
        Guid authorId, int page = 1, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogAuthorBrowseResponse>($"{Root}/authors/{authorId}{PageQuery(page)}", cancellationToken);

    public Task<CatalogBrowseResult<CatalogSeriesBrowseResponse>> GetSeriesAsync(
        Guid seriesId, int page = 1, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogSeriesBrowseResponse>($"{Root}/series/{seriesId}{PageQuery(page)}", cancellationToken);

    public Task<CatalogBrowseResult<CatalogObservedAuthorBrowseResponse>> GetObservedAuthorAsync(
        Guid contextId, Guid subjectId, int page = 1, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogObservedAuthorBrowseResponse>(
            $"{Root}/contexts/{contextId}/authors/{subjectId}{PageQuery(page)}", cancellationToken);

    public Task<CatalogBrowseResult<CatalogObservedSeriesBrowseResponse>> GetObservedSeriesAsync(
        Guid contextId, Guid subjectId, int page = 1, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogObservedSeriesBrowseResponse>(
            $"{Root}/contexts/{contextId}/series/{subjectId}{PageQuery(page)}", cancellationToken);

    /// <summary>The compact cached book for opening another observed record while its provider is unreachable.</summary>
    public Task<CatalogBrowseResult<CatalogObservedBookResponse>> GetObservedBookAsync(
        Guid contextId, string providerId, string externalId, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogObservedBookResponse>(
            $"{Root}/contexts/{contextId}/books/{Uri.EscapeDataString(providerId)}?externalId={Uri.EscapeDataString(externalId)}",
            cancellationToken);

    private static string PageQuery(int page) => page > 1 ? $"?page={page}" : string.Empty;

    private async Task<CatalogBrowseResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            using var response = await httpClient.GetAsync(path, cancellationToken);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                    return value is null
                        ? new CatalogBrowseResult<T>(CatalogBrowseOutcome.Unavailable)
                        : new CatalogBrowseResult<T>(CatalogBrowseOutcome.Ok, value);
                case HttpStatusCode.NotFound:
                    return new CatalogBrowseResult<T>(CatalogBrowseOutcome.NotFound);
                case HttpStatusCode.BadRequest:
                    return new CatalogBrowseResult<T>(CatalogBrowseOutcome.InvalidRequest);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new CatalogBrowseResult<T>(CatalogBrowseOutcome.Unauthorized);
                default:
                    return new CatalogBrowseResult<T>(CatalogBrowseOutcome.Unavailable);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or NotSupportedException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A caller's own cancellation still propagates; only transport faults and timeouts become "unavailable".
            return new CatalogBrowseResult<T>(CatalogBrowseOutcome.Unavailable);
        }
    }
}
