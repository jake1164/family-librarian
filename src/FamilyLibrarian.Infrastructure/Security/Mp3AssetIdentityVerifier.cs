using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Reads an MP3's ID3 tags and compares the book's album, author and language with the Work being fulfilled, so an external
/// MP3 can clear on its content instead of always being held as attested-only. The comparison itself is
/// <see cref="AudioTagIdentity"/>, shared with <see cref="OggAssetIdentityVerifier"/>.
/// </summary>
public sealed class Mp3AssetIdentityVerifier(
    IWorkLookup works, IBookMatcher bookMatcher, IBookRequestFulfillmentStore? requestFulfillment = null) : IAssetIdentityVerifier
{
    public string Id => "mp3-id3-tags";

    public bool Supports(MediaAsset asset) =>
        string.Equals(asset.Format, ".mp3", StringComparison.OrdinalIgnoreCase);

    public async Task<AssetIdentityVerificationResult> VerifyAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        var expected = await works.FindAsync(asset.WorkId, cancellationToken);
        if (expected is null || string.IsNullOrWhiteSpace(expected.Title))
        {
            // Nothing to compare against: behave exactly as before this verifier existed.
            return NoVerifierOutcome.For(asset);
        }

        var tags = await Id3TagReader.ReadAsync(content, cancellationToken);
        var acceptedLanguage = await AcceptedLanguage.FindAsync(requestFulfillment, asset.AssociatedRequestFormatId, cancellationToken);
        return AudioTagIdentity.Evaluate(Id, asset, expected, tags?.ToAudioTags(), acceptedLanguage, bookMatcher);
    }
}

/// <summary>The language the requester accepted for one request format, if any.</summary>
internal static class AcceptedLanguage
{
    public static async Task<string?> FindAsync(
        IBookRequestFulfillmentStore? requestFulfillment, Guid requestFormatId, CancellationToken cancellationToken)
    {
        if (requestFulfillment is null) return null;
        var request = await requestFulfillment.FindByFormatIdAsync(requestFormatId, cancellationToken);
        return request?.Formats.SingleOrDefault(format => format.Id == requestFormatId)?.AcceptedLanguage;
    }
}
