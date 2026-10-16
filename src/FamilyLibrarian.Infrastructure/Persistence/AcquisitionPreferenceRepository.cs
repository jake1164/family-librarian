using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Persistence;

public sealed class AcquisitionPreferenceRepository(AppDbContext database) : IAcquisitionPreferenceStore
{
    public async Task<IReadOnlyList<RequesterAcquisitionPreference>> ListAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        await database.RequesterAcquisitionPreferences.AsNoTracking().Where(p => userIds.Contains(p.UserId))
            .OrderBy(p => p.CreatedAtUtc).ToArrayAsync(cancellationToken);

    public async Task EnsureAsync(RequesterAcquisitionPreference preference, CancellationToken cancellationToken)
    {
        // Two different work locks can save the same person's preference concurrently. The unique key makes retries harmless.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO requests.requester_acquisition_preferences (id, user_id, kind, value, created_at_utc)
            VALUES ({preference.Id}, {preference.UserId}, {preference.Kind.ToString()}, {preference.Value}, {preference.CreatedAtUtc})
            ON CONFLICT (user_id, kind, value) DO NOTHING
            """, cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid userId, Guid preferenceId, CancellationToken cancellationToken) =>
        await database.RequesterAcquisitionPreferences.Where(p => p.UserId == userId && p.Id == preferenceId).ExecuteDeleteAsync(cancellationToken) > 0;
}
