using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Communications;

public sealed class UserQuietHoursLookup(AppDbContext database) : IUserQuietHoursLookup
{
    public async Task<IReadOnlyCollection<Guid>> ListQuietUserIdsAsync(DateTimeOffset utc, CancellationToken cancellationToken)
    {
        var windows = await database.Users.AsNoTracking()
            .Where(user => user.QuietHoursTimeZoneId != null &&
                user.QuietHoursStartMinute != null && user.QuietHoursEndMinute != null)
            .Select(user => new
            {
                user.Id,
                TimeZoneId = user.QuietHoursTimeZoneId!,
                Start = user.QuietHoursStartMinute!.Value,
                End = user.QuietHoursEndMinute!.Value
            })
            .ToListAsync(cancellationToken);

        return windows.Where(window => IsQuiet(window.TimeZoneId, window.Start, window.End, utc))
            .Select(window => window.Id)
            .ToArray();
    }

    private static bool IsQuiet(string timeZoneId, int startMinute, int endMinute, DateTimeOffset utc)
    {
        try
        {
            return new QuietHours(timeZoneId, startMinute, endMinute).IsQuietAt(utc);
        }
        catch (ArgumentException)
        {
            // A saved time zone this host can no longer resolve must not stall everyone's notifications.
            return false;
        }
    }
}
