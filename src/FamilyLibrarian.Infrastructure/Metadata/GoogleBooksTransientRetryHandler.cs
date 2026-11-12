using System.Net;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Retries a Google Books request a couple of times when Google answers with a transient server error. Google documents
/// 5xx answers as "retry with backoff", and on the lab it answered 503 <c>backendFailed</c> to about four requests in
/// seven, so one 503 made Test Connection, and the catalogue search behind it, fail for no fault of the key or the request.
/// </summary>
/// <remarks>
/// <para>Only GET requests with no body are retried, and only for 500, 502, 503 and 504. Anything else, including 429
/// (the quota is the operator's to raise) and every 4xx, is returned at once. After the last attempt the last response
/// is returned unchanged, so a genuine outage still surfaces as the provider's own status.</para>
/// <para>Register it <em>after</em> the API-key handler so the request it clones already carries the key.</para>
/// </remarks>
public sealed class GoogleBooksTransientRetryHandler : DelegatingHandler
{
    /// <summary>The wait before each retry; the number of entries is the number of retries.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays = [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(900)];

    private readonly IReadOnlyList<TimeSpan> delays;

    public GoogleBooksTransientRetryHandler() : this(DefaultDelays)
    {
    }

    public GoogleBooksTransientRetryHandler(IReadOnlyList<TimeSpan> delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        this.delays = delays;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        // A request message can be sent only once, so every attempt after the first sends a copy.
        var response = await base.SendAsync(request, cancellationToken);
        foreach (var delay in delays)
        {
            if (!IsTransient(response.StatusCode))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(delay, cancellationToken);
            using var copy = Clone(request);
            response = await base.SendAsync(copy, cancellationToken);
        }

        return response;
    }

    private static bool IsTransient(HttpStatusCode status) => status is
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}
