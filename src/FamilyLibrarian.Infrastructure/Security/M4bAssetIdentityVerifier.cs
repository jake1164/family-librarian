using System.Buffers.Binary;
using System.Text;
using System.Globalization;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Application.Requests;
using FamilyLibrarian.Application.Security;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Evidence;

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
public sealed class M4bAssetIdentityVerifier(
    IWorkLookup works, IBookMatcher bookMatcher, IBookRequestFulfillmentStore? requestFulfillment = null) : IAssetIdentityVerifier
{
    private const int MaxBoxesPerLevel = 100_000;
    private const int MaxTagItems = 10_000;
    private const int MaxTagValueBytes = 64 * 1024;
    private const int MaxProfileTagValueBytes = 4096;
    private const int MaxTracks = 64;
    private const int MaxLanguageTagBytes = 35;

    // ISO 639-2 values that explicitly say "no usable language": undetermined, no linguistic content,
    // uncoded, multiple. They are unknown, never a mismatch.
    private static readonly string[] UnspecifiedLanguages = ["und", "zxx", "mis", "mul"];

    // 0xA9 is the "(c)" byte QuickTime/iTunes use to prefix text-tag atom names.
    private static readonly string[] TitleTags = ["©nam", "©alb"];
    private static readonly string[] AuthorTags = ["©ART", "aART", "©wrt"];

    public string Id => "m4b-tag-metadata";

    public bool Supports(MediaAsset asset) => Mp4Audio.IsContainerFormat(asset.Format);

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

        var profile = new AudioProfileTags();
        if (tags.TryGetValue("©mvn", out var series)) profile.Add("mvnm", series[0]);
        if (tags.TryGetValue("©mvi", out var position)) profile.Add("mvin", position[0]);
        var profileFacts = profile.Facts();
        var titles = Collect(tags, TitleTags);
        var authors = Collect(tags, AuthorTags);
        if (titles.Count == 0 && authors.Count == 0)
        {
            return AssetIdentityVerificationResult.Unmatched(
                Id, "The audiobook has no embedded title or author tags, so it can't be compared with the catalog entry.", profileFacts);
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
        var editions = titles.Select(AudioEditionMarker.Strip).ToArray();
        // A trailing series designation ("Fourth Wing: Fourth Wing, Book 1") is packaging, not a different title, as
        // long as its number agrees with the catalog's series position; the remaining title must still be exact.
        var titleMatches = titles.Any(title =>
        {
            var core = AudioEditionMarker.Strip(title).CoreTitle;
            var withoutSeries = AudioSeriesDesignation.StripConsistentWith(core, expected.Series ?? []);
            return expectedTitles.Any(expectedTitle =>
                bookMatcher.StrictTitleAuthorMatches(expectedTitle, titleAuthor, core, titleAuthor) ||
                (withoutSeries is not null &&
                 bookMatcher.StrictTitleAuthorMatches(expectedTitle, titleAuthor, withoutSeries, titleAuthor)));
        });
        var authorChecked = authors.Count > 0 && !string.IsNullOrWhiteSpace(expected.PrimaryAuthor);
        var authorMatches = !authorChecked || authors.Any(author => bookMatcher.AuthorMatches(expected.PrimaryAuthor, author));

        // A02: the audio track's own declared language (standard container field only) must not contradict
        // the language accepted for this request. A missing, undetermined, unrecognized or unreadable
        // value is unknown and is never treated as either a match or a mismatch.
        var declaredLanguages = await ReadDeclaredLanguagesSafelyAsync(content, cancellationToken);
        var acceptedLanguage = await FindAcceptedLanguageAsync(asset.AssociatedRequestFormatId, cancellationToken);
        var languageAccepted = declaredLanguages is not { Count: > 0 } ||
            declaredLanguages.Any(language => LanguageAcceptance.IsAcceptedOrUnspecified(language, acceptedLanguage));

        var facts = new List<IdentityFact>
        {
            IdentityFacts.Title(titles.Count > 0, titleMatches),
            IdentityFacts.Author(authorChecked, authorMatches)
        };
        if (declaredLanguages is not null)
        {
            facts.Add(IdentityFacts.Language(declaredLanguages, languageAccepted));
        }
        facts.AddRange(profileFacts);
        if (authorChecked && !authorMatches && titleMatches &&
            ArtifactProfileComparer.HasCorroboration(facts, expected.Series ?? [], expected.Isbn13s) &&
            authors.Any(author => AuthorSpelling.IsVariant(expected.PrimaryAuthor, author)))
        {
            authorMatches = true;
            facts[facts.FindIndex(f => f.FindingId == "ev-author")] = new IdentityFact("ev-author", EvidenceDimension.IdentityCorroboration,
                EvidenceReasonCodes.ArtifactIdentityCorroboration.AuthorSpellingVariant, EvidencePolarity.Weak);
        }
        facts.AddRange(editions.SelectMany(edition => edition.Facts(acceptedLanguage)).Distinct());
        var markerLanguageConflict = editions.Any(edition => edition.IsGerman) &&
            !LanguageAcceptance.IsAcceptedOrUnspecified("de", acceptedLanguage);
        languageAccepted &= !markerLanguageConflict;

        if (titleMatches && authorMatches && languageAccepted)
        {
            return AssetIdentityVerificationResult.Match(Id, facts);
        }

        var reason = titleMatches && authorMatches
            ? string.Empty
            : DescribeMismatch(expectedTitles, expected.PrimaryAuthor, titles, authors, titleMatches, authorMatches);
        if (!languageAccepted)
        {
            var languageReason = (markerLanguageConflict ? "The audiobook's German edition marker conflicts with the language " :
                $"The audiobook's declared language ({string.Join(", ", declaredLanguages!)}) doesn't match the language ") +
                $"accepted for this request{(string.IsNullOrWhiteSpace(acceptedLanguage) ? string.Empty : $" ({acceptedLanguage})")}.";
            reason = reason.Length == 0 ? languageReason : $"{reason} {languageReason}";
        }

        return AssetIdentityVerificationResult.Unmatched(Id, reason, facts);
    }

    private async Task<string?> FindAcceptedLanguageAsync(Guid requestFormatId, CancellationToken cancellationToken)
    {
        if (requestFulfillment is null) return null;
        var request = await requestFulfillment.FindByFormatIdAsync(requestFormatId, cancellationToken);
        return request?.Formats.SingleOrDefault(format => format.Id == requestFormatId)?.AcceptedLanguage;
    }

    /// <summary>
    /// M1-4c: measured properties of the first audio track (duration from its media header, codec from its sample entry,
    /// average bitrate from file size over duration) and the Nero chapter count. Bounded header reads only. A value that
    /// cannot be read is reported as unavailable or not found, never guessed. Independent of identity.
    /// </summary>
    internal static async Task<IReadOnlyList<IdentityFact>> InspectTechnicalAsync(Stream content, CancellationToken cancellationToken)
    {
        double? seconds = null;
        string? codec = null;
        int? chapters = null;
        try
        {
            if (await FindChildAsync(content, 0, content.Length, "moov", cancellationToken) is { } moov)
            {
                var position = moov.PayloadStart;
                var tracks = 0;
                for (var count = 0; count < MaxBoxesPerLevel && seconds is null; count++)
                {
                    if (await TryReadBoxAsync(content, position, moov.End, cancellationToken) is not { } box) break;
                    position = box.End;
                    if (box.Type != "trak") continue;
                    if (++tracks > MaxTracks) break;
                    if (await FindChildAsync(content, box.PayloadStart, box.End, "mdia", cancellationToken) is not { } mdia) continue;
                    if (await ReadHandlerTypeAsync(content, mdia, cancellationToken) != "soun") continue;

                    seconds = await ReadMediaDurationSecondsAsync(content, mdia, cancellationToken);
                    codec = await ReadSampleEntryFormatAsync(content, mdia, cancellationToken);
                }

                if (await FindChildAsync(content, moov.PayloadStart, moov.End, "udta", cancellationToken) is { } udta &&
                    await FindChildAsync(content, udta.PayloadStart, udta.End, "chpl", cancellationToken) is { } chpl)
                {
                    chapters = await ReadNeroChapterCountAsync(content, chpl, cancellationToken);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            // Unreadable parts stay unmeasured.
        }

        List<IdentityFact> facts = [];
        if (seconds is > 0)
        {
            facts.Add(new("ev-audio-duration", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationMeasured,
                EvidencePolarity.Unknown, $"seconds={Math.Round(seconds.Value)};basis=media_header"));
            facts.Add(new("ev-audio-bitrate", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.Bitrate,
                EvidencePolarity.Unknown, $"kbps_avg={Math.Round(content.Length * 8d / seconds.Value / 1000d)};basis=file_size_over_duration"));
        }
        else
        {
            facts.Add(new("ev-audio-duration", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.DurationUnavailable,
                EvidencePolarity.Unknown, "no readable audio media header"));
        }

        if (codec is not null)
        {
            facts.Add(new("ev-audio-codec", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.Codec, EvidencePolarity.Unknown, codec));
        }

        facts.Add(chapters is > 0
            ? new IdentityFact("ev-audio-chapters", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.ChaptersCounted,
                EvidencePolarity.Unknown, chapters.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new IdentityFact("ev-audio-chapters", EvidenceDimension.AudioTechnical, EvidenceReasonCodes.ArtifactAudio.ChaptersNotFound,
                EvidencePolarity.Unknown, "no chapter list in the container"));
        return facts;
    }

    /// <summary><c>mdhd</c>: version 0 has 32-bit timescale at 12 and duration at 16; version 1 has timescale at 20 and a 64-bit duration at 24.</summary>
    private static async Task<double?> ReadMediaDurationSecondsAsync(Stream content, BoxInfo mdia, CancellationToken cancellationToken)
    {
        if (await FindChildAsync(content, mdia.PayloadStart, mdia.End, "mdhd", cancellationToken) is not { } mdhd) return null;
        var available = mdhd.End - mdhd.PayloadStart;
        if (available < 1) return null;

        var toRead = (int)Math.Min(available, 32);
        var buffer = new byte[toRead];
        content.Position = mdhd.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        uint timescale;
        ulong duration;
        if (buffer[0] == 0 && toRead >= 20)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(12, 4));
            duration = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(16, 4));
        }
        else if (buffer[0] == 1 && toRead >= 32)
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(20, 4));
            duration = BinaryPrimitives.ReadUInt64BigEndian(buffer.AsSpan(24, 8));
        }
        else
        {
            return null;
        }

        return timescale == 0 || duration == 0 || duration == uint.MaxValue || duration == ulong.MaxValue ? null : (double)duration / timescale;
    }

    /// <summary><c>minf/stbl/stsd</c>: four bytes version/flags, four of entry count, then the first entry's size and four-character format.</summary>
    private static async Task<string?> ReadSampleEntryFormatAsync(Stream content, BoxInfo mdia, CancellationToken cancellationToken)
    {
        if (await FindChildAsync(content, mdia.PayloadStart, mdia.End, "minf", cancellationToken) is not { } minf ||
            await FindChildAsync(content, minf.PayloadStart, minf.End, "stbl", cancellationToken) is not { } stbl ||
            await FindChildAsync(content, stbl.PayloadStart, stbl.End, "stsd", cancellationToken) is not { } stsd ||
            stsd.End - stsd.PayloadStart < 16)
        {
            return null;
        }

        var buffer = new byte[16];
        content.Position = stsd.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        var format = Encoding.Latin1.GetString(buffer, 12, 4);
        return format.All(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_') ? format.Trim() : null;
    }

    /// <summary>
    /// AUTOFIND 3c: the names of the first <paramref name="maximum"/> chapters of the Nero <c>chpl</c> list (each entry is an
    /// 8-byte start time, a one-byte title length and that many UTF-8 bytes). One bounded read of the atom, never of the media
    /// payload; empty when there is no list or it cannot be read safely. QuickTime text-track chapters are not parsed.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ReadChapterTitlesAsync(
        Stream content, int maximum, CancellationToken cancellationToken)
    {
        const int MaxChplBytes = 64 * 1024;
        var titles = new List<string>();
        try
        {
            if (await FindChildAsync(content, 0, content.Length, "moov", cancellationToken) is not { } moov ||
                await FindChildAsync(content, moov.PayloadStart, moov.End, "udta", cancellationToken) is not { } udta ||
                await FindChildAsync(content, udta.PayloadStart, udta.End, "chpl", cancellationToken) is not { } chpl)
            {
                return titles;
            }

            var available = chpl.End - chpl.PayloadStart;
            if (available < 9) return titles;
            var buffer = new byte[(int)Math.Min(available, MaxChplBytes)];
            content.Position = chpl.PayloadStart;
            await ReadExactAsync(content, buffer, cancellationToken);
            if (buffer[0] != 1) return titles;

            var declared = buffer[8];
            var position = 9;
            for (var index = 0; index < declared && titles.Count < maximum && position + 9 <= buffer.Length; index++)
            {
                var length = buffer[position + 8];
                position += 9;
                if (position + length > buffer.Length) break;
                if (Encoding.UTF8.GetString(buffer, position, length).Trim() is { Length: > 0 } title) titles.Add(title);
                position += length;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            // Unreadable parts stay unmeasured.
        }

        return titles;
    }

    /// <summary>Nero <c>chpl</c> (version 1): four bytes version/flags, four reserved, then a one-byte chapter count.</summary>
    private static async Task<int?> ReadNeroChapterCountAsync(Stream content, BoxInfo chpl, CancellationToken cancellationToken)
    {
        if (chpl.End - chpl.PayloadStart < 9) return null;
        var buffer = new byte[9];
        content.Position = chpl.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        return buffer[0] == 1 ? buffer[8] : null;
    }

    /// <summary>
    /// The recognized, specified languages declared by the audio tracks, or <see langword="null"/> when the
    /// container could not be read for language at all (recorded as not assessed, never as a match).
    /// </summary>
    private static async Task<List<string>?> ReadDeclaredLanguagesSafelyAsync(Stream content, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadDeclaredLanguagesAsync(content, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the standard media language of each <em>audio</em> track: the extended language tag (<c>elng</c>,
    /// BCP 47) when present, otherwise the media header's packed ISO 639-2 code (<c>mdhd</c>). Non-audio tracks
    /// (chapter text, cover) are ignored, as are tags such as a freeform "language" atom, which are not a
    /// standard language field and are never trusted. Nothing is detected from the audio itself.
    /// </summary>
    private static async Task<List<string>> ReadDeclaredLanguagesAsync(Stream content, CancellationToken cancellationToken)
    {
        var languages = new List<string>();
        var moov = await FindChildAsync(content, 0, content.Length, "moov", cancellationToken);
        if (moov is null) return languages;

        var position = moov.Value.PayloadStart;
        var tracks = 0;
        for (var count = 0; count < MaxBoxesPerLevel; count++)
        {
            if (await TryReadBoxAsync(content, position, moov.Value.End, cancellationToken) is not { } box) break;
            position = box.End;
            if (box.Type != "trak") continue;
            if (++tracks > MaxTracks) break;

            if (await FindChildAsync(content, box.PayloadStart, box.End, "mdia", cancellationToken) is not { } mdia) continue;
            if (await ReadHandlerTypeAsync(content, mdia, cancellationToken) != "soun") continue;

            var declared = await ReadExtendedLanguageAsync(content, mdia, cancellationToken)
                ?? await ReadMediaHeaderLanguageAsync(content, mdia, cancellationToken);
            if (declared is not null &&
                !UnspecifiedLanguages.Contains(declared, StringComparer.OrdinalIgnoreCase) &&
                LanguageAcceptance.IsRecognized(declared) &&
                !languages.Contains(declared, StringComparer.OrdinalIgnoreCase))
            {
                languages.Add(declared);
            }
        }

        return languages;
    }

    private static async Task<string?> ReadHandlerTypeAsync(Stream content, BoxInfo mdia, CancellationToken cancellationToken)
    {
        if (await FindChildAsync(content, mdia.PayloadStart, mdia.End, "hdlr", cancellationToken) is not { } hdlr ||
            hdlr.End - hdlr.PayloadStart < 12)
            return null;

        var buffer = new byte[12];
        content.Position = hdlr.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        return Encoding.Latin1.GetString(buffer, 8, 4);
    }

    /// <summary><c>elng</c>: a full box (4 bytes) holding a null-terminated BCP 47 tag.</summary>
    private static async Task<string?> ReadExtendedLanguageAsync(Stream content, BoxInfo mdia, CancellationToken cancellationToken)
    {
        if (await FindChildAsync(content, mdia.PayloadStart, mdia.End, "elng", cancellationToken) is not { } elng) return null;
        var length = (int)Math.Min(elng.End - elng.PayloadStart - 4, MaxLanguageTagBytes);
        if (length <= 0) return null;

        var buffer = new byte[4 + length];
        content.Position = elng.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        var text = Encoding.ASCII.GetString(buffer, 4, length).Split('\0')[0].Trim();
        return text.Length > 0 && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? text : null;
    }

    /// <summary><c>mdhd</c>: version 0 or 1 full box whose packed 15-bit field holds three letters of ISO 639-2.</summary>
    private static async Task<string?> ReadMediaHeaderLanguageAsync(Stream content, BoxInfo mdia, CancellationToken cancellationToken)
    {
        if (await FindChildAsync(content, mdia.PayloadStart, mdia.End, "mdhd", cancellationToken) is not { } mdhd) return null;
        var available = mdhd.End - mdhd.PayloadStart;
        if (available < 1) return null;

        var version = new byte[1];
        content.Position = mdhd.PayloadStart;
        await ReadExactAsync(content, version, cancellationToken);
        var languageOffset = version[0] switch { 0 => 20, 1 => 32, _ => -1 };
        if (languageOffset < 0 || available < languageOffset + 2) return null;

        var packed = new byte[2];
        content.Position = mdhd.PayloadStart + languageOffset;
        await ReadExactAsync(content, packed, cancellationToken);
        var code = BinaryPrimitives.ReadUInt16BigEndian(packed);
        Span<char> letters = stackalloc char[3];
        letters[0] = (char)(((code >> 10) & 0x1F) + 0x60);
        letters[1] = (char)(((code >> 5) & 0x1F) + 0x60);
        letters[2] = (char)((code & 0x1F) + 0x60);
        foreach (var letter in letters)
        {
            if (letter is < 'a' or > 'z') return null;
        }

        return new string(letters);
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
            var profileTag = item.Type is "©mvn" or "©mvi";
            if ((!TitleTags.Contains(item.Type) && !AuthorTags.Contains(item.Type) && !profileTag) ||
                profileTag && result.ContainsKey(item.Type))
            {
                continue;
            }

            var dataPosition = item.PayloadStart;
            while (await TryReadBoxAsync(content, dataPosition, item.End, cancellationToken) is { } data)
            {
                dataPosition = data.End;
                var text = data.Type != "data" ? null : item.Type == "©mvi"
                    ? await ReadMovementIndexAsync(content, data, cancellationToken)
                    : await ReadTextValueAsync(content, data, profileTag ? MaxProfileTagValueBytes : MaxTagValueBytes, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!result.TryGetValue(item.Type, out var values))
                    {
                        result[item.Type] = values = [];
                    }

                    values.Add(text);
                    if (profileTag) break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// <c>data</c> atom layout: 1 byte version, 3 bytes type indicator (1 =
    /// UTF-8 text), 4 bytes locale, then the value. Anything else is ignored.
    /// </summary>
    private static async Task<string?> ReadTextValueAsync(Stream content, BoxInfo data, int maxValueBytes, CancellationToken cancellationToken)
    {
        var valueLength = data.End - data.PayloadStart - 8;
        if (valueLength <= 0 || valueLength > maxValueBytes)
        {
            return null;
        }

        var buffer = new byte[8 + (int)valueLength];
        content.Position = data.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        var typeIndicator = BinaryPrimitives.ReadUInt32BigEndian(buffer) & 0x00FFFFFF;
        return typeIndicator == 1 ? Encoding.UTF8.GetString(buffer, 8, buffer.Length - 8).Trim() : null;
    }

    private static async Task<string?> ReadMovementIndexAsync(Stream content, BoxInfo data, CancellationToken cancellationToken)
    {
        var length = data.End - data.PayloadStart - 8;
        if (length is not (1 or 2 or 3 or 4 or 8)) return null;
        var buffer = new byte[8 + (int)length];
        content.Position = data.PayloadStart;
        await ReadExactAsync(content, buffer, cancellationToken);
        if ((BinaryPrimitives.ReadUInt32BigEndian(buffer) & 0x00FFFFFF) != 21 || (buffer[8] & 0x80) != 0) return null;
        long index = 0;
        foreach (var value in buffer.AsSpan(8)) index = (index << 8) | value;
        return index > 0 ? index.ToString(CultureInfo.InvariantCulture) : null;
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
