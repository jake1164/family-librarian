using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Reads the Vorbis comments of an Ogg Vorbis or Ogg Opus audiobook (<c>.ogg</c>, <c>.oga</c>, <c>.opus</c>) and compares
/// them with the Work being fulfilled by exactly the rules <see cref="Mp3AssetIdentityVerifier"/> applies to ID3 tags:
/// <c>ALBUM</c> is the book, <c>TITLE</c> a track title, <c>ARTIST</c>/<c>ALBUMARTIST</c>/<c>COMPOSER</c>/<c>AUTHOR</c> the
/// author candidates and <c>LANGUAGE</c> the declared language.
/// </summary>
public sealed class OggAssetIdentityVerifier(
    IWorkLookup works, IBookMatcher bookMatcher, IBookRequestFulfillmentStore? requestFulfillment = null) : IAssetIdentityVerifier
{
    public string Id => "ogg-vorbis-comments";

    public bool Supports(MediaAsset asset) =>
        asset.Format.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
        asset.Format.Equals(".oga", StringComparison.OrdinalIgnoreCase) ||
        asset.Format.Equals(".opus", StringComparison.OrdinalIgnoreCase);

    public async Task<AssetIdentityVerificationResult> VerifyAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        var expected = await works.FindAsync(asset.WorkId, cancellationToken);
        if (expected is null || string.IsNullOrWhiteSpace(expected.Title))
        {
            // Nothing to compare against: behave exactly as before this verifier existed.
            return NoVerifierOutcome.For(asset);
        }

        var tags = await OggCommentReader.ReadAsync(content, cancellationToken);
        var acceptedLanguage = await AcceptedLanguage.FindAsync(requestFulfillment, asset.AssociatedRequestFormatId, cancellationToken);
        return AudioTagIdentity.Evaluate(Id, asset, expected, tags, acceptedLanguage, bookMatcher);
    }
}
