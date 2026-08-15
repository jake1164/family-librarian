using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe Audiobookshelf fake: no real network call ever happens in the ordinary test suite.</summary>
internal sealed class AlwaysEmptyAudiobookshelfApiClient : IAudiobookshelfApiClient
{
    public Task<string?> FindExistingItemIdAsync(string title, string? author, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<AudiobookshelfUploadResult> UploadAsync(
        Stream content, string filename, string title, string? author, CancellationToken cancellationToken) =>
        Task.FromResult(new AudiobookshelfUploadResult(true, $"li_test_{Guid.NewGuid():N}", null));
}
