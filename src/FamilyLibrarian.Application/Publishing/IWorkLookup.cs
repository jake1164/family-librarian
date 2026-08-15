namespace FamilyLibrarian.Application.Publishing;

/// <summary>The small slice of a Work's catalog data a publishing destination needs for filenames/metadata.</summary>
public interface IWorkLookup
{
    Task<WorkSummary?> FindAsync(Guid workId, CancellationToken cancellationToken);
}

public sealed record WorkSummary(Guid WorkId, string Title, string? PrimaryAuthor);
