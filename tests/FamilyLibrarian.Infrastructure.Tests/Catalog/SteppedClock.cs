namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// A clock whose time and timers move only when the test advances it. <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// on it completes when <see cref="Advance"/> passes the due time, so a paced wait can be held open and released deterministically.
/// </summary>
internal sealed class SteppedClock : TimeProvider
{
    private readonly DateTimeOffset start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly List<StepTimer> timers = [];
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref ticks));

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    /// <summary>Timers that are armed and not yet fired.</summary>
    public int PendingTimers
    {
        get
        {
            lock (timers)
            {
                return timers.Count(t => t.IsArmed);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new StepTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (timers)
        {
            timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var now = TimeSpan.FromTicks(Interlocked.Add(ref ticks, by.Ticks));
        List<StepTimer> due;
        lock (timers)
        {
            due = [.. timers.Where(t => t.IsArmed && t.DueAt <= now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref ticks));

    private sealed class StepTimer(SteppedClock owner, TimerCallback callback, object? state) : ITimer
    {
        public bool IsArmed { get; private set; }

        public TimeSpan DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            IsArmed = dueTime != Timeout.InfiniteTimeSpan;
            DueAt = owner.Now + dueTime;
            return true;
        }

        public void Fire()
        {
            IsArmed = false;
            callback(state);
        }

        public void Dispose() => IsArmed = false;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
