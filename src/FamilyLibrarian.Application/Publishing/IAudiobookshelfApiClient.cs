using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>Thin wrapper over the Audiobookshelf REST API's upload/search surface.</summary>
public interface IAudiobookshelfApiClient
{
    /// <summary>
    /// Searches the configured library for an item already matching this
    /// title/author, so a retried upload never creates a duplicate. See
    /// <see cref="ICwaCatalogClient.FindBookIdAsync"/> for the same
    /// "ambiguous is not a guess" posture applied to this destination.
    /// </summary>
    Task<BookMatchResult> FindExistingItemIdAsync(string title, string? author, CancellationToken cancellationToken);

    Task<AudiobookshelfUploadResult> UploadAsync(
        Stream content,
        string filename,
        string title,
        string? author,
        CancellationToken cancellationToken);

    /// <summary>
    /// Uploads every track of a multi-file audiobook (e.g. a chaptered
    /// Gutenberg audiobook) in one request, so Audiobookshelf creates a
    /// single library item with all of them rather than one item per track.
    /// </summary>
    Task<AudiobookshelfUploadResult> UploadBundleAsync(
        IReadOnlyList<(Stream Content, string Filename)> tracks,
        string title,
        string? author,
        CancellationToken cancellationToken);
}

public sealed record AudiobookshelfUploadResult(bool Succeeded, string? ExternalItemId, string? Error);
