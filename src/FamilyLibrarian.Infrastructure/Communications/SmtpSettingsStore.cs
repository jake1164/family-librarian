using FamilyLibrarian.Application.Abstractions;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Domain.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Communications;

public sealed class SmtpSettingsStore(AppDbContext database, IClock clock) : ISmtpSettingsStore
{
    public Task<SmtpSettings?> FindAsync(CancellationToken cancellationToken) =>
        database.SmtpSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

    public async Task<SmtpSettings> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var existing = await database.SmtpSettings.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = new SmtpSettings(clock.UtcNow);
        database.SmtpSettings.Add(created);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => database.SaveChangesAsync(cancellationToken);
}
