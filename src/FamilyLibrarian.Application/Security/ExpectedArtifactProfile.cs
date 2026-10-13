using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Security;

public sealed record ExpectedArtifactProfile(
    string Title, IReadOnlyList<string> AlternateTitles, string? PrimaryAuthor,
    IReadOnlyList<BookSeries> Series, IReadOnlyList<string> Isbn13s, string? AcceptedLanguage,
    int? CatalogPageCount, int? CatalogAudioSeconds, bool AcceptedAsCollectionContaining,
    long? PeerMedianSizeBytes = null, int? PeerCount = null, long? AssetSizeBytes = null);

public interface IExpectedArtifactProfileSource
{
    Task<ExpectedArtifactProfile?> FindAsync(MediaAsset asset, CancellationToken cancellationToken);
}

public interface IArtifactPeerProfileSource
{
    Task<PeerSizeEvidence?> FindAsync(MediaAsset asset, CancellationToken cancellationToken);
}

public sealed class ExpectedArtifactProfileSource(
    IWorkLookup works, IBookRequestFulfillmentStore requests, IArtifactPeerProfileSource? peers = null)
    : IExpectedArtifactProfileSource
{
    public async Task<ExpectedArtifactProfile?> FindAsync(MediaAsset asset, CancellationToken cancellationToken)
    {
        var work = await works.FindAsync(asset.WorkId, cancellationToken);
        if (work is null) return null;
        var request = await requests.FindByFormatIdAsync(asset.AssociatedRequestFormatId, cancellationToken);
        var language = request?.Formats.SingleOrDefault(f => f.Id == asset.AssociatedRequestFormatId)?.AcceptedLanguage;
        var peer = peers is null ? null : await peers.FindAsync(asset, cancellationToken);
        return new(work.Title, work.AlternateTitles ?? [], work.PrimaryAuthor, work.Series ?? [], work.Isbn13s,
            language, null, null, false, peer?.MedianSizeBytes, peer?.Count, asset.SizeBytes);
    }
}
