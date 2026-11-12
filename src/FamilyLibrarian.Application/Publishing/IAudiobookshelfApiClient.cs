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
    /// <param name="acceptedLanguage">
    /// Language explicitly accepted for the specific format this verifies.
    /// Other declared languages remain excluded -- see
    /// <see cref="ICwaCatalogClient.FindBookIdAsync"/>.
    /// </param>
    Task<BookMatchResult> FindExistingItemIdAsync(
        string title, string? author, CancellationToken cancellationToken, string? acceptedLanguage = null);

    /// <summary>
    /// Whether the configured token may remove items (the user's delete permission). False when unknown, unreachable or not allowed:
    /// nothing may be retracted on a guess.
    /// </summary>
    Task<bool> CanDeleteAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>
    /// The id of the one library item whose title, or whose upload folder, is exactly this, or null when none or more than one. The folder
    /// counts because Audiobookshelf titles an item from the audio files' tags when they have any. Used to confirm an upgrade copy.
    /// </summary>
    Task<string?> FindItemIdByExactTitleAsync(string exactTitle, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    /// <summary>Removes an item and its files (<c>hard=1</c>); without it the files stay and the next scan brings the item back.</summary>
    Task<bool> DeleteItemHardAsync(string itemId, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Changes an item's title (and nothing else).</summary>
    Task<bool> RenameItemAsync(string itemId, string title, CancellationToken cancellationToken) => Task.FromResult(false);

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
