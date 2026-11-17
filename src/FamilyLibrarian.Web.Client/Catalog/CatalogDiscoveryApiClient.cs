using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyLibrarian.Contracts.Catalog;
using FamilyLibrarian.Web.Client.Authentication;

namespace FamilyLibrarian.Web.Client.Catalog;

public enum LookupOutcome
{
    Ok,

    /// <summary>The temporary context expired, was evicted or is not this user's. All the same answer.</summary>
    NotFound,

    Unauthorized,

    /// <summary>The per-user start rule refused the request; <see cref="LookupResult{T}.RetryAfter"/> says how long.</summary>
    RateLimited,

    /// <summary>A lookup is already running for this user. There is no queue.</summary>
    Busy,

    /// <summary>The host refused with a stable code (source disabled or removed, unsupported, journey ended, nothing to continue).</summary>
    Refused,

    /// <summary>Network failure, server error or an unreadable body. Never carries exception text to the page.</summary>
    Unavailable
}

public sealed record LookupResult<T>(LookupOutcome Outcome, T? Value = null, string? Code = null, TimeSpan? RetryAfter = null)
    where T : class
{
    public bool IsOk => Outcome == LookupOutcome.Ok && Value is not null;
}

/// <summary>
/// Typed client for the explicit source-lookup routes. The two GETs are provider-free reads of the user's own temporary
/// results; the two POSTs are the only calls that can make the host contact a source, and each carries the anti-forgery token.
/// A request names a temporary context and subject the host issued; it never carries a provider id, a URL or a budget.
/// </summary>
public sealed class CatalogDiscoveryApiClient(HttpClient httpClient, AntiforgeryTokenProvider antiforgery)
{
    private const string Root = "api/v1/catalog/discovery";

    public Task<LookupResult<CatalogExpansionOffersResponse>> GetOffersAsync(
        Guid contextId, Guid subjectId, CancellationToken cancellationToken = default) =>
        SendAsync<CatalogExpansionOffersResponse>(
            new HttpRequestMessage(HttpMethod.Get, $"{Root}/contexts/{contextId}/subjects/{subjectId}/offers"), false, cancellationToken);

    public Task<LookupResult<CatalogDiscoveryResponse>> GetWindowAsync(Guid contextId, CancellationToken cancellationToken = default) =>
        SendAsync<CatalogDiscoveryResponse>(new HttpRequestMessage(HttpMethod.Get, $"{Root}/windows/{contextId}"), false, cancellationToken);

    public Task<LookupResult<CatalogDiscoveryResponse>> ExpandAsync(
        Guid contextId, Guid subjectId, string kind, CancellationToken cancellationToken = default) =>
        SendAsync<CatalogDiscoveryResponse>(
            new HttpRequestMessage(HttpMethod.Post, $"{Root}/expansions")
            {
                Content = JsonContent.Create(new CatalogExpansionRequest(contextId, subjectId, kind))
            },
            true, cancellationToken);

    public Task<LookupResult<CatalogDiscoveryResponse>> ContinueAsync(Guid contextId, CancellationToken cancellationToken = default) =>
        SendAsync<CatalogDiscoveryResponse>(
            new HttpRequestMessage(HttpMethod.Post, $"{Root}/expansions/continue")
            {
                Content = JsonContent.Create(new CatalogExpansionContinueRequest(contextId))
            },
            true, cancellationToken);

    private async Task<LookupResult<T>> SendAsync<T>(HttpRequestMessage request, bool withToken, CancellationToken cancellationToken)
        where T : class
    {
        using (request)
        {
            try
            {
                if (withToken) await antiforgery.AttachAsync(request, cancellationToken);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
                        return value is null ? new LookupResult<T>(LookupOutcome.Unavailable) : new LookupResult<T>(LookupOutcome.Ok, value);
                    case HttpStatusCode.NotFound:
                        return new LookupResult<T>(LookupOutcome.NotFound);
                    case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                        return new LookupResult<T>(LookupOutcome.Unauthorized);
                    case HttpStatusCode.TooManyRequests:
                        var code = await ReadCodeAsync(response, cancellationToken);
                        return code == "lookup-running"
                            ? new LookupResult<T>(LookupOutcome.Busy, Code: code)
                            : new LookupResult<T>(LookupOutcome.RateLimited, Code: code, RetryAfter: response.Headers.RetryAfter?.Delta);
                    case HttpStatusCode.Conflict:
                        return new LookupResult<T>(LookupOutcome.Refused, Code: await ReadCodeAsync(response, cancellationToken));
                    case HttpStatusCode.BadRequest:
                        // A rejected token is a refresh-and-retry condition for the next attempt, not a data error.
                        antiforgery.Invalidate();
                        return new LookupResult<T>(LookupOutcome.Refused, Code: "invalid-request");
                    default:
                        return new LookupResult<T>(LookupOutcome.Unavailable);
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or NotSupportedException ||
                (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // A caller's own cancellation still propagates; only transport faults and timeouts become "unavailable".
                return new LookupResult<T>(LookupOutcome.Unavailable);
            }
        }
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<CatalogExpansionProblemResponse>(cancellationToken))?.Code;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
