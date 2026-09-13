using System.Net;
using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Retries a Hardcover request using the delay the API itself asks for.
/// </summary>
/// <remarks>
/// This is the first status-code-reactive backoff handler in this codebase —
/// <see cref="OpenLibraryRateLimitHandler"/> only ever spaces requests apart
/// proactively on a fixed interval and never inspects the response. Hardcover
/// documents a guaranteed <c>Retry-After</c> header on every <c>429</c>
/// response, so this handler trusts that value (capped, in case of a
/// misbehaving or unexpectedly large value) rather than guessing a delay or
/// implementing exponential backoff of its own.
/// </remarks>
internal sealed class HardcoverRateLimitHandler(IOptions<HardcoverMetadataOptions> options)
    : DelegatingHandler
{
    private readonly HardcoverMetadataOptions _options = options.Value;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var requestToSend = attempt == 0 ? request : await CloneAsync(request, cancellationToken);
            var response = await base.SendAsync(requestToSend, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests ||
                attempt >= _options.MaxRetryAttempts)
            {
                return response;
            }

            var delay = GetRetryDelay(response);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response)
    {
        var maximum = TimeSpan.FromSeconds(_options.MaxRetryDelaySeconds);
        var retryAfterHeader = response.Headers.RetryAfter;
        TimeSpan? retryAfter = retryAfterHeader?.Delta
            ?? (retryAfterHeader?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        if (retryAfter is null || retryAfter <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        return retryAfter > maximum ? maximum : retryAfter.Value;
    }

    // A sent HttpRequestMessage cannot be sent again, so a retry needs its own
    // copy. This client never sets HttpRequestOptions on these requests, so
    // only the method, URI, headers, and body need to survive the clone.
    private static async Task<HttpRequestMessage> CloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version
        };

        if (request.Content is not null)
        {
            var buffer = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(buffer);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
