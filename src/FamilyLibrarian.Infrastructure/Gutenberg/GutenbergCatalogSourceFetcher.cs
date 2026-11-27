namespace FamilyLibrarian.Infrastructure.Gutenberg;

/// <summary>A successful response and the position of the source that served it.</summary>
internal sealed record GutenbergCatalogSourceResponse(HttpResponseMessage Response, int SourceIndex, Uri Source);

/// <summary>
/// Requests a catalogue resource from an ordered list of equivalent sources and
/// moves to the next source when one is unavailable. Project Gutenberg's public
/// website and its content mirrors publish the same catalogue paths, so any
/// source that answers successfully is acceptable.
/// </summary>
internal static class GutenbergCatalogSourceFetcher
{
    /// <summary>
    /// Tries each source in turn, beginning at <paramref name="startIndex"/> and
    /// wrapping around, and returns the first successful response. Callers pass
    /// the index that last succeeded so a long run does not re-probe a source
    /// that is known to be down for every request.
    /// </summary>
    public static async Task<GutenbergCatalogSourceResponse> GetAsync(
        HttpClient httpClient,
        IReadOnlyList<Uri> sources,
        TimeSpan responseTimeout,
        HttpCompletionOption completionOption,
        int startIndex,
        CancellationToken cancellationToken,
        Func<Uri, CancellationToken, Task>? sourceAttempt = null)
    {
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        var failures = new List<string>(sources.Count);
        Exception? lastException = null;
        for (var offset = 0; offset < sources.Count; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = (startIndex + offset) % sources.Count;
            var source = sources[index];
            if (sourceAttempt is not null) await sourceAttempt(source, cancellationToken);
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(responseTimeout);
            try
            {
                var response = await httpClient.GetAsync(source, completionOption, attemptTimeout.Token);
                // Headers have arrived; the timeout must not interrupt the body.
                attemptTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                if (response.IsSuccessStatusCode)
                {
                    return new GutenbergCatalogSourceResponse(response, index, source);
                }

                failures.Add($"{source.Host} returned {(int)response.StatusCode} ({response.StatusCode})");
                lastException = new HttpRequestException(
                    $"Response status code does not indicate success: {(int)response.StatusCode} ({response.StatusCode}).",
                    inner: null,
                    response.StatusCode);
                response.Dispose();
            }
            catch (HttpRequestException exception)
            {
                failures.Add($"{source.Host} failed: {exception.Message}");
                lastException = exception;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The per-source timeout (or the client timeout) fired, not the caller.
                failures.Add($"{source.Host} did not respond within {responseTimeout.TotalSeconds:0} seconds");
                lastException = exception;
            }
        }

        // HttpRequestException is what the synchronizer treats as a retryable
        // network failure, so the existing retry loop still applies once every
        // source has been tried.
        var statusCode = (lastException as HttpRequestException)?.StatusCode;
        throw new HttpRequestException(
            sources.Count == 1
                ? failures[0] + "."
                : $"All {sources.Count} Project Gutenberg catalogue sources failed: {string.Join("; ", failures)}.",
            lastException,
            statusCode);
    }
}
