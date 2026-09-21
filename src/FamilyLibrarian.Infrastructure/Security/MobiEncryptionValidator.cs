using System.Buffers.Binary;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Rejects encrypted MOBI-family artifacts even when a provider incorrectly
/// reported them DRM-free. This is a second line of defense after the
/// provider's pre-download DRM evidence; it never decrypts or attempts to
/// circumvent DRM.
/// </summary>
public sealed class MobiEncryptionValidator : IAssetValidator
{
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".prc"
    };

    public string Id => "mobi-encryption";

    public async Task<ValidationOutcome> ValidateAsync(
        MediaAsset asset, Stream content, CancellationToken cancellationToken)
    {
        if (!SupportedFormats.Contains(asset.Format))
        {
            return new ValidationOutcome(true, null);
        }

        if (!content.CanSeek)
        {
            return new ValidationOutcome(false, "The MOBI-family file cannot be inspected for encryption.");
        }

        var header = new byte[82];
        if (await ReadAtLeastAsync(content, header, cancellationToken) != header.Length ||
            !header.AsSpan(60, 8).SequenceEqual("BOOKMOBI"u8))
        {
            return new ValidationOutcome(false, "The file is not a recognizable MOBI-family ebook.");
        }

        var recordZeroOffset = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(78, 4));
        if (recordZeroOffset > content.Length - 16)
        {
            return new ValidationOutcome(false, "The MOBI-family file has an invalid first-record offset.");
        }

        content.Position = recordZeroOffset + 12;
        var encryptionBytes = new byte[2];
        if (await ReadAtLeastAsync(content, encryptionBytes, cancellationToken) != encryptionBytes.Length)
        {
            return new ValidationOutcome(false, "The MOBI-family file has an incomplete encryption header.");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(encryptionBytes) == 0
            ? new ValidationOutcome(true, null)
            : new ValidationOutcome(false, "The MOBI-family file is encrypted or DRM-protected.");
    }

    private static async Task<int> ReadAtLeastAsync(Stream content, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await content.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
