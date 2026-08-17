using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Infrastructure.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Re-sniffs a staged file's real bytes against its claimed format at the
/// security-evaluation gate — <c>FileTypeValidator</c> from
/// <c>docs/03-provider-api-contracts.md</c> §8.
/// </summary>
/// <remarks>
/// <see cref="FileSystemAssetStagingStore.WriteToQuarantineAsync"/> already
/// runs this same sniff once, at staging time, and rejects a mismatch before a
/// <see cref="MediaAsset"/> row is even created. This is not redundant with
/// that: quarantine has no browser access and no path to Trusted, but nothing
/// stops a future caller from staging content differently, and the recorded
/// <see cref="Domain.Security.SecurityEvaluation.ValidationResults"/> should
/// reflect what the security gate itself verified, not only what happened
/// earlier during upload. Applies to every registered format, not just EPUB —
/// unlike <see cref="EpubValidator"/>, there is no unsupported case to skip.
/// </remarks>
public sealed class FileTypeValidator : IAssetValidator
{
    public string Id => "file-type";

    public async Task<ValidationOutcome> ValidateAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        if (!KnownFormatContentTypes.ByExtension.TryGetValue(asset.Format, out var expectedContentType))
        {
            return new ValidationOutcome(false, $"'{asset.Format}' is not a recognized format.");
        }

        var header = new byte[SignatureFileTypeDetector.RequiredHeaderLength];
        var headerBytesRead = await ReadHeaderAsync(content, header, cancellationToken);
        var detectedContentType = SignatureFileTypeDetector.Detect(header, headerBytesRead);

        return string.Equals(detectedContentType, expectedContentType, StringComparison.OrdinalIgnoreCase)
            ? new ValidationOutcome(true, null)
            : new ValidationOutcome(
                false,
                $"The file's contents ({detectedContentType}) do not match its format ({expectedContentType}).");
    }

    private static async Task<int> ReadHeaderAsync(Stream content, byte[] header, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        int bytesRead;
        while (totalRead < header.Length &&
            (bytesRead = await content.ReadAsync(header.AsMemory(totalRead), cancellationToken)) > 0)
        {
            totalRead += bytesRead;
        }

        return totalRead;
    }
}
