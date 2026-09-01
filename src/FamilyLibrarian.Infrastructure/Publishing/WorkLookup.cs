using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class WorkLookup(AppDbContext database) : IWorkLookup
{
    public async Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken)
    {
        var query =
            from work in database.Works.AsNoTracking()
            where work.Id == workId
            select new WorkSummary(
                work.Id,
                work.CanonicalTitle,
                work.Authors
                    .OrderBy(author => author.Ordinal)
                    .Select(author => author.Author.CanonicalName)
                    .FirstOrDefault(),
                work.Editions
                    .Where(edition => edition.Isbn13 != null)
                    .Select(edition => edition.Isbn13!)
                    .Distinct()
                    .ToList());

        return await query.SingleOrDefaultAsync(cancellationToken);
    }
}
