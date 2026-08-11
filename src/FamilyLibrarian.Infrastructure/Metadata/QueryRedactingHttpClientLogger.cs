using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Logs outbound provider calls without their query string.
/// </summary>
/// <remarks>
/// Google Books authenticates with <c>?key=</c>, and the logging handler nearest
/// the socket runs *after* <see cref="GoogleBooksApiKeyHandler"/> has appended
/// it, so the request URI reaching the logger carries the API key.
/// <para>
/// The built-in <c>IHttpClientFactory</c> loggers already redact query values to
/// <c>?*</c>, so this is not closing an open leak. It makes the guarantee
/// explicit and local instead of resting on a framework default that a future
/// version or a logging reconfiguration could change — the provider contract in
/// <c>docs/03-provider-api-contracts.md</c> requires that secrets never reach
/// logs, and this client is the one that carries a secret in its URI. It drops
/// the query entirely rather than substituting a marker, and logs at Debug so
/// routine provider traffic stays out of Information-level output.
/// </para>
/// </remarks>
public sealed class QueryRedactingHttpClientLogger(ILogger<QueryRedactingHttpClientLogger> logger)
    : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request)
    {
        // Guarded because Redact allocates. The redacted value is hoisted into a
        // local so CA1873 sees a cheap argument rather than a call it cannot
        // prove is guarded.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var uri = Redact(request.RequestUri);
            ProviderHttpLog.RequestStarting(logger, request.Method.Method, uri);
        }

        return null;
    }

    public void LogRequestStop(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan elapsed)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var uri = Redact(request.RequestUri);
        ProviderHttpLog.RequestCompleted(
            logger,
            request.Method.Method,
            uri,
            (int)response.StatusCode,
            elapsed.TotalMilliseconds);
    }

    public void LogRequestFailed(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed)
    {
        var uri = Redact(request.RequestUri);
        ProviderHttpLog.RequestFailed(
            logger,
            request.Method.Method,
            uri,
            elapsed.TotalMilliseconds,
            exception);
    }

    /// <summary>Returns scheme/host/path only. Never returns the query string.</summary>
    public static string Redact(Uri? uri)
    {
        if (uri is null)
        {
            return "(no uri)";
        }

        if (!uri.IsAbsoluteUri)
        {
            // A relative URI has no components to pick apart; take everything
            // before the first '?' rather than risk echoing the query.
            var raw = uri.OriginalString;
            var separator = raw.IndexOf('?', StringComparison.Ordinal);
            return separator < 0 ? raw : raw[..separator];
        }

        return uri.GetLeftPart(UriPartial.Path);
    }
}

internal static partial class ProviderHttpLog
{
    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Debug,
        Message = "Sending {Method} {Uri}")]
    internal static partial void RequestStarting(ILogger logger, string method, string uri);

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Debug,
        Message = "Received {StatusCode} for {Method} {Uri} in {ElapsedMilliseconds}ms")]
    internal static partial void RequestCompleted(
        ILogger logger,
        string method,
        string uri,
        int statusCode,
        double elapsedMilliseconds);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "{Method} {Uri} failed after {ElapsedMilliseconds}ms")]
    internal static partial void RequestFailed(
        ILogger logger,
        string method,
        string uri,
        double elapsedMilliseconds,
        Exception exception);
}
