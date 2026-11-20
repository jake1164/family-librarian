using System.Text;

namespace FamilyLibrarian.Application.Catalog.Observation;

/// <summary>A continuation that is not a key of the category it was sent with. Never silently read as "start again".</summary>
public sealed class InvalidEvidenceCursorException : ArgumentException
{
    public InvalidEvidenceCursorException()
        : base("The continuation is not valid.")
    {
    }

    public InvalidEvidenceCursorException(string message)
        : base(message)
    {
    }

    public InvalidEvidenceCursorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The opaque continuation of an issue-report page: the last key the previous page ended at. It is versioned and carries its
/// category, so a key of one category can never be read as another's, and the decoded shape (how many parts, which are ids, how
/// long a text key may be) is validated here, once, for the reader and for the endpoint that has to answer 400 before any query runs.
/// </summary>
/// <remarks>
/// The token is lossless: each part is written length-prefixed as strict UTF-8, so no key is truncated and no character (including a
/// separator-like control character) can be confused with structure. A text part may be as long as the model column it was read from
/// (a Work or Series normalized key 1,000 characters, an Author's 512). The worst token is therefore bounded:
/// <see cref="EvidenceInspectionLimits.MaximumCursorLength"/> is derived from those maxima, which is why the encoder, the decoder and
/// the endpoint can never disagree about what is legal.
/// </remarks>
public static class EvidenceCursor
{
    private const string Version = "v1";

    /// <summary>The longest normalized Work or Series key the catalog model stores (characters).</summary>
    public const int MaximumTitleKeyLength = 1_000;

    /// <summary>The longest normalized Author key the catalog model stores (characters).</summary>
    public const int MaximumAuthorKeyLength = 512;

    // The strict UTF-8 form of a UTF-16 string is at most three bytes per UTF-16 code unit. A byte-length prefix is at most two bytes
    // for any part this class accepts (a 7-bit-encoded length under 16,384), and the header names the version and the longest category.
    private const int HeaderAllowanceBytes = 64;

    /// <summary>The longest token <see cref="Encode"/> can issue, in base64url characters, for any category and any legal key.</summary>
    public const int WorstCaseTokenLength = (((MaximumTitleKeyLength + MaximumAuthorKeyLength) * 3 + (2 * 2) + HeaderAllowanceBytes) * 4 + 2) / 3;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly record struct Part(bool IsId, int MaximumLength);

    private static readonly Part Id = new(true, 36);

    private static Part[] ShapeOf(EvidenceIssueCategory category) => category switch
    {
        EvidenceIssueCategory.PossibleDuplicates => [new(false, MaximumTitleKeyLength), new(false, MaximumAuthorKeyLength)],
        EvidenceIssueCategory.SameNameAuthors => [new(false, MaximumAuthorKeyLength)],
        EvidenceIssueCategory.SameNameSeries => [new(false, MaximumTitleKeyLength)],
        EvidenceIssueCategory.ProvisionalCredits => [Id, Id],
        _ => [Id]
    };

    /// <summary>The cursor that makes the next page start strictly after the given key.</summary>
    /// <exception cref="ArgumentException">The key is not a legal key of the category (wrong shape, an id that is not one, or longer than the model allows).</exception>
    public static string Encode(EvidenceIssueCategory category, params string[] parts)
    {
        var shape = ShapeOf(category);
        if (parts.Length != shape.Length || parts.Where((part, index) => !IsLegal(shape[index], part)).Any())
        {
            throw new ArgumentException("The key does not fit the category's continuation.", nameof(parts));
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(category.ToString());
            foreach (var part in parts)
            {
                writer.Write(part);
            }
        }

        return Convert.ToBase64String(stream.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>The key a cursor carries, or null for no cursor (the first page). Anything else that is not a key of this category throws.</summary>
    /// <exception cref="InvalidEvidenceCursorException">The cursor is malformed, from another version or category, or the wrong shape.</exception>
    public static string[]? Decode(EvidenceIssueCategory category, string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        if (cursor.Length > EvidenceInspectionLimits.MaximumCursorLength)
        {
            throw new InvalidEvidenceCursorException();
        }

        var shape = ShapeOf(category);
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var bytes = Convert.FromBase64String(padded);

            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
            if (reader.ReadString() != Version || reader.ReadString() != category.ToString())
            {
                throw new InvalidEvidenceCursorException();
            }

            var parts = new string[shape.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                parts[index] = reader.ReadString();
                if (!IsLegal(shape[index], parts[index]))
                {
                    throw new InvalidEvidenceCursorException();
                }
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidEvidenceCursorException();
            }

            return parts;
        }
        catch (Exception exception) when (exception is (FormatException or ArgumentException or IOException) and not InvalidEvidenceCursorException)
        {
            // FormatException: not base64 or a bad length prefix. IOException/EndOfStreamException: a length prefix past the end.
            // ArgumentException: strict UTF-8 refused the bytes.
            throw new InvalidEvidenceCursorException("The continuation is not valid.", exception);
        }
    }

    /// <summary>Whether a cursor (or none) is a key of the category.</summary>
    public static bool IsValid(EvidenceIssueCategory category, string? cursor)
    {
        try
        {
            _ = Decode(category, cursor);
            return true;
        }
        catch (InvalidEvidenceCursorException)
        {
            return false;
        }
    }

    private static bool IsLegal(Part shape, string part) =>
        part.Length <= shape.MaximumLength
        && (!shape.IsId || (Guid.TryParseExact(part, "D", out var id) && id != Guid.Empty));
}
