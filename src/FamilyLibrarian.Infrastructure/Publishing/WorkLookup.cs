using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FamilyLibrarian.Infrastructure.Publishing;

public sealed class WorkLookup(AppDbContext database) : IWorkLookup
{
    public async Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken)
    {
        var work = await database.Works
            .AsNoTracking()
            .Include(work => work.Authors).ThenInclude(workAuthor => workAuthor.Author)
            .Include(work => work.Editions)
            .Include(work => work.SeriesEntries).ThenInclude(seriesEntry => seriesEntry.Series)
            .FirstOrDefaultAsync(work => work.Id == workId, cancellationToken);
        if (work is null)
        {
            return null;
        }

        var orderedAuthors = work.Authors.OrderBy(author => author.Ordinal).ToArray();
        // FL has no "primary edition" concept -- the first one created is a
        // best-effort source for the fields that live only on an Edition,
        // not a per-format-authoritative choice.
        var firstEdition = work.Editions.OrderBy(edition => edition.CreatedAtUtc).FirstOrDefault();

        return new WorkSummary(
            work.Id,
            work.CanonicalTitle,
            orderedAuthors.FirstOrDefault()?.Author.CanonicalName,
            work.Editions.Where(edition => edition.Isbn13 != null).Select(edition => edition.Isbn13!).Distinct().ToArray(),
            orderedAuthors.Select(workAuthor => new BookAuthor(workAuthor.Author.CanonicalName, workAuthor.Role)).ToArray(),
            work.SeriesEntries.Select(entry => new BookSeries(entry.Series.Name, entry.PositionLabel)).ToArray(),
            firstEdition?.Language,
            firstEdition?.PublicationDate?.Year,
            firstEdition?.Publisher);
    }
}
