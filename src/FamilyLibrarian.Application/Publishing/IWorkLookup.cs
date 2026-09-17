using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>The slice of a Work's catalog data a publishing destination or an external-provider search needs.</summary>
public interface IWorkLookup
{
    Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken);
}

/// <param name="Isbn13s">
/// Every distinct ISBN-13 known across the Work's editions, in no particular
/// order. Empty when the Work has no editions with a recorded ISBN.
/// </param>
/// <param name="Authors">Every credited author, ordered. Empty when the Work has none recorded.</param>
/// <param name="Series">Every series this Work belongs to. Empty when none recorded.</param>
/// <param name="Language">
/// From the Work's first-created Edition, if any -- FL has no "primary
/// edition" concept, so this is a best-effort pick, not an authoritative
/// per-format choice.
/// </param>
/// <param name="PublicationYear">Same best-effort source as <paramref name="Language"/>.</param>
/// <param name="Publisher">Same best-effort source as <paramref name="Language"/>.</param>
public sealed record WorkSummary(
    Guid WorkId,
    string Title,
    string? PrimaryAuthor,
    IReadOnlyList<string> Isbn13s,
    IReadOnlyList<BookAuthor>? Authors = null,
    IReadOnlyList<BookSeries>? Series = null,
    string? Language = null,
    int? PublicationYear = null,
    string? Publisher = null);
