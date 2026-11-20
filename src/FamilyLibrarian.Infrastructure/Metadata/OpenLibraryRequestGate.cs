using Microsoft.Extensions.Options;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// Paces every Open Library request (search, detail and discovery share one instance, hence one pace and one memory of
/// throttling) and remembers a 429. Once Open Library has answered 429, no request is sent until the cooldown ends: the next
/// callers are refused locally with the same 429 and the remaining wait, so a throttled source is not asked again by every
/// operation in turn. The cooldown is the response's own <c>Retry-After</c>, honoured in full when it sent a usable one,
/// otherwise an application default that is <b>not</b> a published Open Library quota. How long one operation is willing to
/// wait is the caller's own budget decision (it is told the remaining time and reports a wait that does not fit); it never
/// shortens when later callers may ask again. Host-memory and single-process.
/// </summary>
internal sealed class OpenLibraryRequestGate(IOptions<OpenLibraryMetadataOptions> options, TimeProvider? clock = null)
    : IDisposable
{
    /// <summary>Used when a 429 carries no usable <c>Retry-After</c>. An application choice, not a stated Open Library limit.</summary>
    internal static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = clock ?? TimeProvider.System;
    private readonly Lock _cooldownLock = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly TimeSpan _minimumInterval = TimeSpan.FromSeconds(
        1d / options.Value.RequestsPerSecond);
    private DateTimeOffset _nextRequestAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _cooldownUntilUtc = DateTimeOffset.MinValue;

    /// <summary>How long no request may be sent because Open Library recently answered 429; zero when none is in force.</summary>
    public TimeSpan RemainingCooldown()
    {
        lock (_cooldownLock)
        {
            var remaining = _cooldownUntilUtc - _time.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>The gate's own clock, so a <c>Retry-After</c> HTTP-date is measured against the same time the cooldown is kept on.</summary>
    public DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>
    /// Records a 429 from its <c>Retry-After</c> header: delay-seconds, or an HTTP-date measured against this gate's clock.
    /// </summary>
    public void NoteThrottled(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter) =>
        NoteThrottled(retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - _time.GetUtcNow() : null));

    /// <summary>
    /// Records a 429. The stated wait is kept in full: a later or longer cooldown replaces an earlier one, a shorter one never
    /// shortens it, and an absent, zero, negative or past value uses <see cref="DefaultCooldown"/>. A wait too large to
    /// represent as an instant becomes the latest representable instant (the source stays closed) rather than overflowing.
    /// </summary>
    public void NoteThrottled(TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } stated && stated > TimeSpan.Zero ? stated : DefaultCooldown;
        lock (_cooldownLock)
        {
            var now = _time.GetUtcNow();
            var until = wait >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + wait;
            if (until > _cooldownUntilUtc) _cooldownUntilUtc = until;
        }
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            var delay = _nextRequestAtUtc - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken);
            }

            _nextRequestAtUtc = _time.GetUtcNow() + _minimumInterval;
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
        // A recent 429 is remembered for every caller of the shared instance: nothing is sent while it is in force.
        if (Refusal(request) is { } refusedEarly) return refusedEarly;

        await requestGate.WaitAsync(cancellationToken);

        // Pacing may have queued this request behind one that was then answered 429. The cooldown is checked again after
        // the wait and immediately before dispatch, so a request that was admitted before the 429 arrived is not sent after it.
        if (Refusal(request) is { } refusedAfterPacing) return refusedAfterPacing;

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            requestGate.NoteThrottled(response.Headers.RetryAfter);
        }

        return response;
    }

    private HttpResponseMessage? Refusal(HttpRequestMessage request)
    {
        var cooling = requestGate.RemainingCooldown();
        if (cooling <= TimeSpan.Zero) return null;

        // Retry-After delay-seconds is a 32-bit count; a longer remaining wait is reported as the largest value it can carry.
        var seconds = Math.Min(Math.Ceiling(cooling.TotalSeconds), int.MaxValue);
        var refused = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { RequestMessage = request };
        refused.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return refused;
    }
}
