using System.Net;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The provider asked Family Librarian to slow down (HTTP 429, or 503 with <c>Retry-After</c>; protocol v2 §7).
/// Still an <see cref="HttpRequestException"/> so every existing handler that degrades a failed search keeps
/// working; only the recheck looks for the subtype to stop sending further searches and honour the wait.
/// </summary>
public sealed class ExternalProviderRateLimitedException : HttpRequestException
{
    /// <summary>The longest wait a provider can impose; a larger value is clamped.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    public ExternalProviderRateLimitedException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base(HttpRequestError.Unknown, "The provider asked to be retried later.", inner: null, statusCode)
    {
        RetryAfter = retryAfter is { } wait
            ? wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait
            : null;
    }

    /// <summary>The provider's requested wait, clamped to 0-24 hours; null when it gave none.</summary>
    public TimeSpan? RetryAfter { get; }
}
