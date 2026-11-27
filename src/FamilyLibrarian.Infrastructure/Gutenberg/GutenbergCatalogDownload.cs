namespace FamilyLibrarian.Infrastructure.Gutenberg;

internal static class GutenbergCatalogDownload
{
    // An actively transferring large catalogue may take much longer than two
    // minutes overall. Only a read that receives no bytes for this long fails.
    public static async Task<long> CopyAsync(Stream source, Stream destination, TimeProvider timeProvider,
        Func<long, CancellationToken, Task>? progress, CancellationToken cancellationToken,
        TimeSpan? inactivityTimeout = null)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        var lastReported = timeProvider.GetUtcNow();
        while (true)
        {
            using var idle = new CancellationTokenSource(inactivityTimeout ?? TimeSpan.FromMinutes(2), timeProvider);
            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
            int read;
            try { read = await source.ReadAsync(buffer, readCancellation.Token); }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && idle.IsCancellationRequested)
            {
                throw new IOException("The catalogue download stopped receiving data. The source did not send any bytes before the inactivity timeout.", exception);
            }
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            if (progress is not null && timeProvider.GetUtcNow() - lastReported >= TimeSpan.FromSeconds(1))
            {
                await progress(total, cancellationToken);
                lastReported = timeProvider.GetUtcNow();
            }
        }
        if (progress is not null) await progress(total, cancellationToken);
        return total;
    }
}
