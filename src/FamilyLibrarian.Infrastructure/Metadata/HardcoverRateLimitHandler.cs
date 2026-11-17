using System.Net;
using FamilyLibrarian.Application.Catalog.Discovery;
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
internal sealed class HardcoverRateLimitHandler(
    IOptions<HardcoverMetadataOptions> options,
    HardcoverRateState? rateState = null,
    Func<TimeSpan, CancellationToken, Task>? pauseFor = null) : DelegatingHandler
{
    /// <summary>
    /// How much of an announced wait may remain after a pause before it is treated as a new wait. A timer can fire a few
    /// milliseconds before the rate state's wall-clock reset instant, so a pause for exactly the announced wait can return with a
    /// sliver left; that sliver is waited out, while a genuinely new wait (another request used the slot) is not.
    /// </summary>
    internal static readonly TimeSpan PauseRoundingTolerance = TimeSpan.FromMilliseconds(250);

    private const int MaximumPausePasses = 3;

    private readonly HardcoverMetadataOptions _options = options.Value;
    private readonly HardcoverRateState _rateState = rateState ?? new HardcoverRateState();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = pauseFor ?? ((wait, token) => Task.Delay(wait, token));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // A discovery request carries its server-owned budget. It never inherits this handler's general retry policy: the
        // budget decides every attempt and the single permitted retry.
        if (request.Options.TryGetValue(HardcoverGraphQlClient.DiscoveryBudgetKey, out var budget))
        {
            return await SendWithinBudgetAsync(request, budget, cancellationToken);
        }

        var maximumPause = TimeSpan.FromSeconds(_options.MaxRetryDelaySeconds);
        for (var attempt = 0; ; attempt++)
        {
            // Every request made with the shared credential asks the shared state first, so ordinary traffic honours a wait
            // Hardcover has already announced, and cannot spend a slot a concurrent request already holds. A short wait is
            // served; one beyond the retry cap is reported as the rate-limit failure it is, without a request.
            var (reservation, blockedFor) = await ReserveAsync(wait => wait <= maximumPause, cancellationToken);
            if (reservation is null)
            {
                return RateLimited(request, blockedFor);
            }

            var response = await SendReservedAsync(request, attempt, reservation, cancellationToken);

            if (response.StatusCode != HttpStatusCode.TooManyRequests ||
                attempt >= _options.MaxRetryAttempts)
            {
                return response;
            }

            var delay = GetRetryDelay(response);
            response.Dispose();
            await _delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Every physical attempt is reserved from <paramref name="budget"/> before it is dispatched. A 429 is retried at most
    /// once, and only when Hardcover's own <c>Retry-After</c> fits the time left: a longer wait is reported, never shortened.
    /// A wait Hardcover has already announced for an emptied bucket is honoured the same way.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithinBudgetAsync(
        HttpRequestMessage request, IDiscoveryBudget budget, CancellationToken cancellationToken)
    {
        HttpResponseMessage? throttled = null;
        for (var attempt = 0; ; attempt++)
        {
            var (reservation, _) = await ReserveAsync(wait => wait < budget.RemainingWork, cancellationToken);
            if (reservation is null)
            {
                return throttled ?? throw new DiscoveryTransportRefusedException(null, DiscoveryCodes.RetryAfterTooLong);
            }

            var admission = budget.TryAdmitAttempt(isRetry: attempt > 0);
            if (admission != AttemptAdmission.Admitted)
            {
                reservation.Dispose();
                return throttled ?? throw new DiscoveryTransportRefusedException(admission, string.Empty);
            }

            throttled?.Dispose();
            throttled = null;
            var response = await SendReservedAsync(request, attempt, reservation, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var delay = GetRetryDelay(response, capped: false);
            if (delay >= budget.RemainingWork)
            {
                return response;
            }

            throttled = response;
            await _delay(delay, cancellationToken);
        }
    }

    /// <summary>
    /// Asks the shared state for a slot, finishing one announced wait within a bounded allowance when <paramref name="mayPause"/>
    /// allows it. Scheduled pauses use whole milliseconds rounded up, so a positive remainder cannot become a completed
    /// zero-duration delay. Both the caller's budget and the allowance apply to the actual scheduled duration.
    /// </summary>
    private async Task<(HardcoverRateState.Reservation? Reservation, TimeSpan Wait)> ReserveAsync(
        Func<TimeSpan, bool> mayPause, CancellationToken cancellationToken)
    {
        var admission = _rateState.TryReserve();
        if (admission.Reservation is not null || !mayPause(admission.Wait))
        {
            return admission;
        }

        // One announced wait is paused for, and nothing more: the loop only finishes a sliver the timer left (see
        // PauseRoundingTolerance), never a new wait, and the total never exceeds the announced wait plus that tolerance.
        var allowance = admission.Wait + PauseRoundingTolerance;
        var paused = TimeSpan.Zero;
        for (var pass = 0; pass < MaximumPausePasses; pass++)
        {
            // Task.Delay truncates a TimeSpan to whole milliseconds. Finish fractional remainders instead of spending all
            // passes on immediate completions, without rounding past the caller's remaining work or this wait's allowance.
            var scheduled = TimeSpan.FromMilliseconds(Math.Ceiling(admission.Wait.TotalMilliseconds));
            if (!mayPause(scheduled) || paused + scheduled > allowance)
            {
                return admission;
            }

            await _delay(scheduled, cancellationToken);
            paused += scheduled;
            admission = _rateState.TryReserve();
            if (admission.Reservation is not null ||
                admission.Wait > PauseRoundingTolerance ||
                paused + admission.Wait > allowance ||
                !mayPause(admission.Wait))
            {
                return admission;
            }
        }

        return admission;
    }

    // The reservation is settled, together with whatever the response says about the allowance, on every path. Everything that
    // can fail before dispatch (building the retry copy, a token already cancelled) happens inside the try and leaves the
    // reservation unmarked, so it is returned unspent. Once marked, a transport failure or cancellation is charged: the server
    // may have counted the request even though no answer came back.
    private async Task<HttpResponseMessage> SendReservedAsync(
        HttpRequestMessage original, int attempt, HardcoverRateState.Reservation reservation, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            var request = attempt == 0 ? original : await CloneAsync(original, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            reservation.MarkDispatched();
            response = await base.SendAsync(request, cancellationToken);
            return response;
        }
        finally
        {
            reservation.Complete(response);
        }
    }

    // The failure an exhausted announced bucket would have produced anyway, without spending a request on it.
    private static HttpResponseMessage RateLimited(HttpRequestMessage request, TimeSpan wait)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromSeconds(Math.Ceiling(wait.TotalSeconds)));
        return response;
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, bool capped = true)
    {
        var maximum = TimeSpan.FromSeconds(_options.MaxRetryDelaySeconds);
        var retryAfterHeader = response.Headers.RetryAfter;
        TimeSpan? retryAfter = retryAfterHeader?.Delta
            ?? (retryAfterHeader?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        if (retryAfter is null || retryAfter <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        return capped && retryAfter > maximum ? maximum : retryAfter.Value;
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

        // Options carry the discovery budget; a retried copy must be charged to the same one.
        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        }

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

/// <summary>Thrown by the pipeline when the budget refuses an attempt before dispatch. No request was sent.</summary>
internal sealed class DiscoveryTransportRefusedException(AttemptAdmission? admission, string code)
    : InvalidOperationException("The discovery budget refused the request.")
{
    public AttemptAdmission? Admission { get; } = admission;

    public string Code { get; } = code;
}
