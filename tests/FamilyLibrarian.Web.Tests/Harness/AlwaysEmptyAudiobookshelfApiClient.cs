using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe Audiobookshelf fake: no real network call ever happens in the ordinary test suite.</summary>
internal sealed class AlwaysEmptyAudiobookshelfApiClient : IAudiobookshelfApiClient
{
    public Task<BookMatchResult> FindExistingItemIdAsync(string title, string? author, CancellationToken cancellationToken) =>
        Task.FromResult(BookMatchResult.NoMatchResult);

    public Task<AudiobookshelfUploadResult> UploadAsync(
        Stream content, string filename, string title, string? author, CancellationToken cancellationToken) =>
        Task.FromResult(new AudiobookshelfUploadResult(true, $"li_test_{Guid.NewGuid():N}", null));

    public Task<AudiobookshelfUploadResult> UploadBundleAsync(
        IReadOnlyList<(Stream Content, string Filename)> tracks,
        string title,
        string? author,
        CancellationToken cancellationToken) =>
        Task.FromResult(new AudiobookshelfUploadResult(true, $"li_test_{Guid.NewGuid():N}", null));
}
