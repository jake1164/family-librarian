using System.Net;
using FamilyLibrarian.Application.Catalog.Discovery;

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

    /// <summary>
    /// Carries a discovery request's server-owned budget down to this handler. A request that holds it never inherits the
    /// two-retry policy above: the budget admits every physical attempt, including the single permitted retry.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<IDiscoveryBudget> DiscoveryBudgetKey = new("FamilyLibrarian.DiscoveryBudget");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (request.Options.TryGetValue(DiscoveryBudgetKey, out var budget))
        {
            return await SendWithinBudgetAsync(request, budget, cancellationToken);
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

    /// <summary>
    /// Every physical attempt is reserved from <paramref name="budget"/> before it is dispatched, so the retry delays above
    /// bound the shape of a retry but never its number: the budget's own retry and attempt ceilings decide. The retry is
    /// reserved while the transient answer is still held, so a refusal returns that real answer. A <c>Retry-After</c> the
    /// provider sent is honoured when it is longer than the planned delay, never shortened, and a wait that does not fit the
    /// remaining provider-work time ends the operation with the answer already received instead of retrying early.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithinBudgetAsync(
        HttpRequestMessage request, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        var admission = budget.TryAdmitAttempt(isRetry: false);
        if (admission != AttemptAdmission.Admitted)
        {
            throw new DiscoveryTransportRefusedException(admission, string.Empty);
        }

        var response = await base.SendAsync(request, cancellationToken);
        for (var retry = 0; retry < delays.Count; retry++)
        {
            if (!IsTransient(response.StatusCode))
            {
                return response;
            }

            var delay = delays[retry];
            if (RetryAfterOf(response) is { } announced && announced > delay)
            {
                delay = announced;
            }

            if (delay >= budget.RemainingWork || budget.TryAdmitAttempt(isRetry: true) != AttemptAdmission.Admitted)
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

    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } known && known > TimeSpan.Zero ? known : null;
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

        // Options carry the discovery budget; a retried copy must be charged to the same one.
        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        }

        return copy;
    }
}
