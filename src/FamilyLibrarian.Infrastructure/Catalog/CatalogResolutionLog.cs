using FamilyLibrarian.Application.Catalog;
using Microsoft.Extensions.Logging;

namespace FamilyLibrarian.Infrastructure.Catalog;

/// <summary>
/// Logs what a committed catalog creation could not bind to an existing record. Identifiers only, and bounded: no names,
/// provider payloads or SQL, and never more than <see cref="MaximumLoggedIds"/> namesake ids per entry.
/// </summary>
public sealed partial class CatalogResolutionLog(ILogger<CatalogResolutionLog> logger) : ICatalogResolutionDiagnostics
{
    private const int MaximumLoggedIds = 10;

    public void Report(Guid workId, CatalogResolutionDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        foreach (var credit in diagnostics.AmbiguousAuthors)
        {
            LogAmbiguousAuthor(
                workId, credit.NewAuthorId, credit.NamesakeAuthorIds.Count,
                string.Join(',', credit.NamesakeAuthorIds.Take(MaximumLoggedIds)));
        }

        foreach (var omitted in diagnostics.OmittedSeriesMemberships)
        {
            LogOmittedSeries(
                workId, omitted.NamesakeSeriesIds.Count,
                string.Join(',', omitted.NamesakeSeriesIds.Take(MaximumLoggedIds)));
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Catalog Work {WorkId} was credited to an author name shared by {NamesakeCount} existing authors ({NamesakeIds}); it received its own new Author {NewAuthorId} and no namesake gained the credit.")]
    private partial void LogAmbiguousAuthor(Guid workId, Guid newAuthorId, int namesakeCount, string namesakeIds);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Catalog Work {WorkId} reports a series name shared by {NamesakeCount} existing series ({NamesakeIds}); no series membership was created.")]
    private partial void LogOmittedSeries(Guid workId, int namesakeCount, string namesakeIds);
}
