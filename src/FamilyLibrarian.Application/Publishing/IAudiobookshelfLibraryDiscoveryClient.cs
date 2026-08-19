namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Lists an Audiobookshelf instance's libraries and folders so the settings
/// UI can offer them as a pick-list instead of requiring the administrator to
/// find raw IDs in Audiobookshelf's own UI or database.
/// </summary>
public interface IAudiobookshelfLibraryDiscoveryClient
{
    Task<AudiobookshelfLibraryDiscoveryOutcome> ListLibrariesAsync(
        string baseUrl, string apiToken, CancellationToken cancellationToken);
}

public sealed record AudiobookshelfFolderInfo(string Id, string Path);

public sealed record AudiobookshelfLibraryInfo(string Id, string Name, IReadOnlyList<AudiobookshelfFolderInfo> Folders);

public sealed record AudiobookshelfLibraryDiscoveryOutcome(
    bool Succeeded, string? Error, IReadOnlyList<AudiobookshelfLibraryInfo> Libraries)
{
    public static AudiobookshelfLibraryDiscoveryOutcome Failure(string error) => new(false, error, []);

    public static AudiobookshelfLibraryDiscoveryOutcome Success(IReadOnlyList<AudiobookshelfLibraryInfo> libraries) =>
        new(true, null, libraries);
}
