using System.Globalization;

namespace FamilyLibrarian.Infrastructure.Metadata;

/// <summary>
/// What Hardcover last said about the shared credential's remaining allowance, read from its documented
/// <c>RateLimit</c> response header (for example <c>"Free";r=8;t=0, "daily";r=4231;t=51234</c>), plus the requests this process
/// has sent and not yet heard back from. Every request made with the credential (search, detail, language lookup, discovery)
/// feeds it and asks it for admission, so one caller cannot spend the slot another has already been promised. Host-memory
/// only and single-process: it makes no distributed-accounting claim, and the numbers are Hardcover's current statement, not
/// constants stored here.
/// </summary>
/// <remarks>
/// Admission is conservative: a reserved request counts against every announced bucket until its response (or failure) is
/// known, so concurrent callers cannot all pass on the same announced "r=1". A bucket whose reset time has passed describes a
/// window that no longer exists and constrains nothing, and with no announcement at all nothing is invented.
/// <para>
/// Only a reservation that was definitely never sent is handed back. Once a request has been dispatched, the server may have
/// counted it whatever happens next, so a response without a usable header for a bucket, a transport failure and a cancellation
/// all leave that bucket charged one slot until its window resets. Inside one window the reported count only ever falls: a
/// header that reports more than is already known is older evidence delivered late, not a refill, so it cannot reopen a window
/// a newer response has exhausted. Only the window's own reset (by this process's clock) lets the count rise again.
/// </para>
/// The state is keyed to the process, not to a particular token: after an administrator replaces the credential, an old
/// announcement can pace the new token until it resets, which is the safe direction.
/// </remarks>
public sealed class HardcoverRateState(TimeProvider? clock = null)
{
    public const string HeaderName = "RateLimit";

    private readonly Lock gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.OrdinalIgnoreCase);
    private int inFlight;

    /// <summary>Records the buckets in a response's <c>RateLimit</c> header. An absent or malformed header changes nothing.</summary>
    public void Observe(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (gate)
        {
            ApplyHeader(response, time.GetUtcNow());
        }
    }

    /// <summary>
    /// Asks for admission of one request. When a slot is available it is reserved (counted against every announced bucket until
    /// the returned <see cref="Reservation"/> completes) and the wait is zero. When an announced bucket has no slot left,
    /// nothing is reserved and the wait is how long until every such bucket has refilled.
    /// </summary>
    public (Reservation? Reservation, TimeSpan Wait) TryReserve()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            var wait = WaitLocked(now);
            if (wait > TimeSpan.Zero)
            {
                return (null, wait);
            }

            inFlight++;
            return (new Reservation(this), TimeSpan.Zero);
        }
    }

    /// <summary>How long until every bucket with no free slot has refilled; zero when none is known to be exhausted.</summary>
    public TimeSpan RequiredWait()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            return WaitLocked(now);
        }
    }

    private TimeSpan WaitLocked(DateTimeOffset now)
    {
        var wait = TimeSpan.Zero;
        foreach (var bucket in buckets.Values)
        {
            if (bucket.ResetsAtUtc <= now)
            {
                continue;
            }

            if (bucket.Remaining - bucket.Unconfirmed - inFlight <= 0 && bucket.ResetsAtUtc - now > wait)
            {
                wait = bucket.ResetsAtUtc - now;
            }
        }

        return wait;
    }

    private void Release(HttpResponseMessage? response, bool dispatched)
    {
        lock (gate)
        {
            if (inFlight > 0)
            {
                inFlight--;
            }

            var now = time.GetUtcNow();

            // Recorded in the same step as the release, so another caller never sees a response's count and its own slot
            // counted twice.
            var reported = response is null ? [] : ApplyHeader(response, now);

            // A request that reached the transport may have been counted by the server whatever came back, so every window
            // still in force that the response did not report on stays charged for it. A definitely unsent one charges nothing.
            if (dispatched || response is not null)
            {
                Charge(reported, now);
            }
        }
    }

    private void Charge(HashSet<string> reported, DateTimeOffset now)
    {
        foreach (var (name, bucket) in buckets.ToArray())
        {
            if (bucket.ResetsAtUtc > now && !reported.Contains(name))
            {
                buckets[name] = bucket with { Unconfirmed = bucket.Unconfirmed + 1 };
            }
        }
    }

    /// <summary>Applies the header's usable entries and returns the bucket names it reported on.</summary>
    private HashSet<string> ApplyHeader(HttpResponseMessage response, DateTimeOffset now)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!response.Headers.TryGetValues(HeaderName, out var values))
        {
            return seen;
        }

        foreach (var value in values)
        {
            foreach (var entry in value.Split(','))
            {
                if (!TryParseEntry(entry, out var name, out var remaining, out var resetSeconds))
                {
                    continue;
                }

                var resetsAt = now + TimeSpan.FromSeconds(resetSeconds);
                seen.Add(name);
                if (buckets.TryGetValue(name, out var current) && current.ResetsAtUtc > now)
                {
                    // Same window by our own clock: the count may only fall (a larger one is an older response arriving late),
                    // and the earliest reset estimate is the tightest one, since each is no earlier than the true reset.
                    buckets[name] = current with
                    {
                        Remaining = Math.Min(current.Remaining, remaining),
                        ResetsAtUtc = resetsAt < current.ResetsAtUtc ? resetsAt : current.ResetsAtUtc
                    };
                }
                else
                {
                    buckets[name] = new Bucket(remaining, resetsAt, 0);
                }
            }
        }

        // A bucket this header leaves out is not forgotten while its window is in force: the omission may belong to an older
        // response. Windows that have ended are dropped.
        foreach (var ended in buckets.Where(pair => pair.Value.ResetsAtUtc <= now && !seen.Contains(pair.Key)).Select(pair => pair.Key).ToArray())
        {
            buckets.Remove(ended);
        }

        return seen;
    }

    private static bool TryParseEntry(string entry, out string name, out int remaining, out int resetSeconds)
    {
        name = string.Empty;
        remaining = 0;
        resetSeconds = 0;
        var open = entry.IndexOf('"', StringComparison.Ordinal);
        var close = open < 0 ? -1 : entry.IndexOf('"', open + 1);
        if (close < 0)
        {
            return false;
        }

        name = entry[(open + 1)..close];
        var haveRemaining = false;
        var haveReset = false;
        foreach (var part in entry[(close + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("r=", StringComparison.Ordinal) &&
                int.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out remaining))
            {
                haveRemaining = true;
            }
            else if (part.StartsWith("t=", StringComparison.Ordinal) &&
                int.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out resetSeconds))
            {
                haveReset = true;
            }
        }

        return name.Length > 0 && haveRemaining && haveReset;
    }

    // Unconfirmed counts dispatched requests the window's last statement may not include; they are charged on top of it.
    private readonly record struct Bucket(int Remaining, DateTimeOffset ResetsAtUtc, int Unconfirmed);

    /// <summary>
    /// One admitted, unanswered request. Call <see cref="MarkDispatched"/> immediately before the request can reach the
    /// transport, then complete it with the response, or dispose it when no response arrived. A reservation that was never
    /// marked is returned unspent; a marked one without a usable header stays charged.
    /// </summary>
    public sealed class Reservation : IDisposable
    {
        private readonly HardcoverRateState state;
        private int dispatched;
        private int finished;

        internal Reservation(HardcoverRateState state) => this.state = state;

        /// <summary>From here on the server may count this request, so a lost or unreadable outcome is charged, not refunded.</summary>
        public void MarkDispatched() => Volatile.Write(ref dispatched, 1);

        /// <summary>
        /// Returns the slot and records the response's <c>RateLimit</c> header (when it has one) in one step. A response means
        /// the request was sent; with no response it is charged only if <see cref="MarkDispatched"/> was called.
        /// </summary>
        public void Complete(HttpResponseMessage? response)
        {
            if (Interlocked.Exchange(ref finished, 1) == 0)
            {
                state.Release(response, Volatile.Read(ref dispatched) == 1);
            }
        }

        public void Dispose() => Complete(null);
    }
}
