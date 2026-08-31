using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Communications;

public sealed class UserEmailLookup(AppDbContext database) : IUserEmailLookup
{
    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        database.Users
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .FirstOrDefaultAsync(cancellationToken);
}
