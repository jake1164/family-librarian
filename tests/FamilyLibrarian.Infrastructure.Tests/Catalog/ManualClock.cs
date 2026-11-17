namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>A clock tests move by hand; timestamps and UTC time advance together.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly DateTimeOffset start = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref ticks));

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
}
