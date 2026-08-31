namespace FamilyLibrarian.Application.Publishing;

/// <summary>The small slice of a Work's catalog data a publishing destination needs for filenames/metadata.</summary>
public interface IWorkLookup
{
    Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken);
}

/// <param name="Isbn13s">
/// Every distinct ISBN-13 known across the Work's editions, in no particular
/// order. Empty when the Work has no editions with a recorded ISBN.
/// </param>
public sealed record WorkSummary(Guid WorkId, string Title, string? PrimaryAuthor, IReadOnlyList<string> Isbn13s);
