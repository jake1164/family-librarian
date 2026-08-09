using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Metadata;

internal sealed class OpenLibraryRequestGate(IOptions<OpenLibraryMetadataOptions> options)
    : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly TimeSpan _minimumInterval = TimeSpan.FromSeconds(
        1d / options.Value.RequestsPerSecond);
    private DateTimeOffset _nextRequestAtUtc = DateTimeOffset.MinValue;

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var now = TimeProvider.System.GetUtcNow();
            var delay = _nextRequestAtUtc - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, TimeProvider.System, cancellationToken);
            }

            _nextRequestAtUtc = TimeProvider.System.GetUtcNow() + _minimumInterval;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose() => _mutex.Dispose();
}

internal sealed class OpenLibraryRateLimitHandler(OpenLibraryRequestGate requestGate)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
