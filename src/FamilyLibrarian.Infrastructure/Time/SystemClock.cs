using FamilyLibrarian.Application.Abstractions;

namespace FamilyLibrarian.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
