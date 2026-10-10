using System.Buffers.Binary;
using System.Text;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Infrastructure.Security;

/// <summary>
/// Reads an M4B audiobook's iTunes-style metadata tags (<c>moov/udta/meta/ilst</c>)
/// and requires the embedded title, and the embedded author when one is
/// present, to match the Work being fulfilled.
/// </summary>
/// <remarks>
/// Conservative in the same way as <see cref="EpubAssetIdentityVerifier"/>:
/// missing, unreadable, or different metadata is held as unmatched with a
/// reason a librarian can act on, never guessed into a match. The one
/// deliberate difference is that a file with <em>no</em> author tag is judged on
/// its title alone, because audiobook tools frequently omit or repurpose the
/// artist tag (narrator), and author is not an identity prerequisite for
/// external acquisition. An author tag that is present but matches nothing is
/// still a mismatch.
/// <para>
/// Hand-rolled and bounded, like <see cref="AudioValidator"/>: every box is
/// reached by seeking, never by reading the (multi-hundred-megabyte) media
/// payload, box counts and tag sizes are capped, and a malformed container
/// ends in an unmatched result rather than an exception.
/// </para>
/// </remarks>
public sealed class M4bAssetIdentityVerifier(IWorkLookup works, IBookMatcher bookMatcher) : IAssetIdentityVerifier
{
    private const int MaxBoxesPerLevel = 100_000;
    private const int MaxTagItems = 10_000;
    private const int MaxTagValueBytes = 64 * 1024;

    // 0xA9 is the "(c)" byte QuickTime/iTunes use to prefix text-tag atom names.
    private static readonly string[] TitleTags = ["©nam", "©alb"];
    private static readonly string[] AuthorTags = ["©ART", "aART", "©wrt"];

    public string Id => "m4b-tag-metadata";

    public bool Supports(MediaAsset asset) =>
        string.Equals(asset.Format, ".m4b", StringComparison.OrdinalIgnoreCase);

    public async Task<AssetIdentityVerificationResult> VerifyAsync(
        MediaAsset asset,
        Stream content,
        CancellationToken cancellationToken)
    {
        var expected = await works.FindAsync(asset.WorkId, cancellationToken);
        if (expected is null || string.IsNullOrWhiteSpace(expected.Title))
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The catalog entry for this book has no title on file to compare against.");
        }

        if (!content.CanSeek)
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The audiobook could not be opened in a way that allows its tags to be read.");
        }

        IReadOnlyDictionary<string, List<string>> tags;
        try
        {
            tags = await ReadTagsAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The audiobook's metadata could not be read safely -- it may be malformed.");
        }

        var titles = Collect(tags, TitleTags);
        var authors = Collect(tags, AuthorTags);
        if (titles.Count == 0 && authors.Count == 0)
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The audiobook has no embedded title or author tags, so it can't be compared with the catalog entry.");
        }

        var expectedTitles = new[] { expected.Title }
            .Concat(expected.AlternateTitles ?? [])
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // This is a trust boundary: the title must be the same work, not merely
        // start with it ("Dune" must not accept "Dune Messiah"). The strict
        // comparison is exact on the normalized title. It requires an author on
        // both sides, so the expected author stands in for the observed one here
        // -- the author tags are judged separately below, and a file with none
        // is judged on its exact title alone.
        var titleAuthor = string.IsNullOrWhiteSpace(expected.PrimaryAuthor) ? "Unknown" : expected.PrimaryAuthor;
        var titleMatches = titles.Any(title => expectedTitles.Any(expectedTitle =>
            bookMatcher.StrictTitleAuthorMatches(expectedTitle, titleAuthor, title, titleAuthor)));
        var authorChecked = authors.Count > 0 && !string.IsNullOrWhiteSpace(expected.PrimaryAuthor);
        var authorMatches = !authorChecked || authors.Any(author => bookMatcher.AuthorMatches(expected.PrimaryAuthor, author));

        return titleMatches && authorMatches
            ? AssetIdentityVerificationResult.Match(Id)
            : AssetIdentityVerificationResult.Unmatched(Id, DescribeMismatch(
                expectedTitles, expected.PrimaryAuthor, titles, authors, titleMatches, authorMatches));
    }

    private static List<string> Collect(IReadOnlyDictionary<string, List<string>> tags, string[] names) =>
        names.Where(tags.ContainsKey)
            .SelectMany(name => tags[name])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string DescribeMismatch(
        IReadOnlyList<string> expectedTitles,
        string? expectedAuthor,
        List<string> foundTitles,
        List<string> foundAuthors,
        bool titleMatches,
        bool authorMatches)
    {
        var parts = new List<string>();
        if (!titleMatches)
        {
            parts.Add(foundTitles.Count == 0
                ? $"the file has no embedded title (catalog expects \"{string.Join("\" / \"", expectedTitles)}\")"
                : $"the file's embedded title is \"{string.Join("\" / \"", foundTitles)}\" but the catalog expects one of \"{string.Join("\" / \"", expectedTitles)}\"");
        }

        if (!authorMatches)
        {
            parts.Add($"the file's embedded author/artist is \"{string.Join("\" / \"", foundAuthors)}\" but the catalog expects \"{expectedAuthor}\" " +
                "(audiobook tags sometimes hold only the narrator)");
        }

        return $"Embedded audiobook tags didn't match the catalog entry: {string.Join("; ", parts)}.";
    }

    /// <summary>
    /// Returns the UTF-8 text values of every <c>ilst</c> item, keyed by atom
    /// name. Empty when the file has no tag list at all.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, List<string>>> ReadTagsAsync(
        Stream content, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var length = content.Length;

        var moov = await FindChildAsync(content, 0, length, "moov", cancellationToken);
        if (moov is null)
        {
            return result;
        }

        var udta = await FindChildAsync(content, moov.Value.PayloadStart, moov.Value.End, "udta", cancellationToken);
        if (udta is null)
        {
            return result;
        }

        var meta = await FindChildAsync(content, udta.Value.PayloadStart, udta.Value.End, "meta", cancellationToken);
        if (meta is null)
        {
            return result;
        }

        // "meta" is a full box: a 4-byte version/flags field precedes its children.
        var metaChildrenStart = meta.Value.PayloadStart + 4;
        if (metaChildrenStart > meta.Value.End)
        {
            throw new InvalidDataException("The meta box is truncated.");
        }

        var ilst = await FindChildAsync(content, metaChildrenStart, meta.Value.End, "ilst", cancellationToken);
        if (ilst is null)
        {
            return result;
        }

        var itemCount = 0;
        var position = ilst.Value.PayloadStart;
        while (await TryReadBoxAsync(content, position, ilst.Value.End, cancellationToken) is { } item)
        {
            if (++itemCount > MaxTagItems)
            {
                throw new InvalidDataException("The tag list has too many items.");
            }

            position = item.End;
            if (!TitleTags.Contains(item.Type) && !AuthorTags.Contains(item.Type))
            {
                continue;
            }

            var dataPosition = item.PayloadStart;
            while (await TryReadBoxAsync(content, dataPosition, item.End, cancellationToken) is { } data)
            {
                dataPosition = data.End;
                var text = data.Type == "data" ? await ReadTextValueAsync(content, data, cancellationToken) : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!result.TryGetValue(item.Type, out var values))
                    {
                        result[item.Type] = values = [];
                    }

                    values.Add(text);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <c>data</c> atom layout: 1 byte version, 3 bytes type indicator (1 =
    /// UTF-8 text), 4 bytes locale, then the value. Anything else is ignored.
    /// </summary>
    private static async Task<string?> ReadTextValueAsync(Stream content, BoxInfo data, CancellationToken cancellationToken)
    {
        var valueLength = data.End - data.PayloadStart - 8;
        if (valueLength <= 0 || valueLength > MaxTagValueBytes)
        {
            return null;
        }

        var buffer = new byte[8 + (int)valueLength];
        content.Position = data.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        var typeIndicator = BinaryPrimitives.ReadUInt32BigEndian(buffer) & 0x00FFFFFF;
        return typeIndicator == 1 ? Encoding.UTF8.GetString(buffer, 8, buffer.Length - 8).Trim() : null;
    }

    private static async Task<BoxInfo?> FindChildAsync(
        Stream content, long start, long end, string type, CancellationToken cancellationToken)
    {
        var position = start;
        for (var count = 0; count < MaxBoxesPerLevel; count++)
        {
            if (await TryReadBoxAsync(content, position, end, cancellationToken) is not { } box)
            {
                return null;
            }

            if (box.Type == type)
            {
                return box;
            }

            position = box.End;
        }

        throw new InvalidDataException("The container has too many boxes at one level.");
    }

    /// <returns>The box starting at <paramref name="position"/>, or <c>null</c> at the end of the parent.</returns>
    private static async Task<BoxInfo?> TryReadBoxAsync(
        Stream content, long position, long limit, CancellationToken cancellationToken)
    {
        if (limit - position < 8)
        {
            return null;
        }

        content.Position = position;
        var header = new byte[8];
        await ReadExactAsync(content, header, cancellationToken);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        var type = Encoding.Latin1.GetString(header, 4, 4);
        var headerSize = 8L;

        if (size == 1)
        {
            if (limit - position < 16)
            {
                throw new InvalidDataException("A box header is truncated.");
            }

            var large = new byte[8];
            await ReadExactAsync(content, large, cancellationToken);
            var largeSize = BinaryPrimitives.ReadUInt64BigEndian(large);
            if (largeSize > long.MaxValue)
            {
                throw new InvalidDataException("A box declares an impossible size.");
            }

            size = (long)largeSize;
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = limit - position;
        }

        if (size < headerSize || position + size > limit)
        {
            throw new InvalidDataException($"The box '{type}' has an invalid size.");
        }

        return new BoxInfo(type, position + headerSize, position + size);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += bytesRead;
        }
    }

    private readonly record struct BoxInfo(string Type, long PayloadStart, long End);
}
