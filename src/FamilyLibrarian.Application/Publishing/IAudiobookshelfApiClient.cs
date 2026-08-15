namespace FamilyLibrarian.Application.Publishing;

/// <summary>Thin wrapper over the Audiobookshelf REST API's upload/search surface.</summary>
public interface IAudiobookshelfApiClient
{
    /// <summary>
    /// Searches the configured library for an item already matching this
    /// title/author, so a retried upload never creates a duplicate.
    /// </summary>
    Task<string?> FindExistingItemIdAsync(string title, string? author, CancellationToken cancellationToken);

    Task<AudiobookshelfUploadResult> UploadAsync(
        Stream content,
        string filename,
        string title,
        string? author,
        CancellationToken cancellationToken);
}

public sealed record AudiobookshelfUploadResult(bool Succeeded, string? ExternalItemId, string? Error);
