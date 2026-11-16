using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Observation;

/// <summary>
/// Writes the normalized, bounded evidence the catalog keeps: claim values and observation snapshots. The text is deterministic
/// (fixed property order, invariant culture, bounded strings), so the same evidence always hashes to the same value and a repeat
/// is recognised instead of stored again. Raw provider payloads and credentials never pass through here.
/// </summary>
/// <remarks>
/// Every budget is a budget of serialized UTF-8 bytes (16 KiB per claim value, 64 KiB per snapshot), because that is what is stored
/// and sent: a character count undercounts multibyte text, and JSON escapes make some characters longer still. Anything shortened
/// to fit says so with <c>"truncated": true</c>, is cut only between whole characters, and is still valid JSON.
/// </remarks>
internal static class EvidenceJson
{
    /// <summary>The snapshot layout version.</summary>
    internal const int SchemaVersion = 1;

    private const int MaximumText = 512;

    /// <summary>The reductions tried in order until a snapshot fits its byte budget: how many editions, credits, subjects and series, and how long a string.</summary>
    private static readonly SnapshotShape[] SnapshotShapes =
    [
        new(100, 50, 20, 20, MaximumText),
        new(25, 20, 10, 10, 256),
        new(5, 5, 5, 5, 128),
        new(0, 2, 2, 2, 64)
    ];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>How an HTTP response renders JSON by default: every non-ASCII character as a six-byte escape.</summary>
    private static readonly JsonWriterOptions RenderedOptions = new() { Indented = false, Encoder = JavaScriptEncoder.Default };

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static int Utf8Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// The size of compact JSON as PostgreSQL's <c>jsonb</c> hands it back: the same UTF-8 plus the one space it adds after every
    /// structural colon and comma. The writer's budgets are measured on this, so a value that was within budget when written is still
    /// within it when read back, whichever representation is checked.
    /// </summary>
    internal static int StoredBytes(string json)
    {
        var separators = 0;
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }
            }
            else if (character == '"')
            {
                inString = true;
            }
            else if (character is ':' or ',')
            {
                separators++;
            }
        }

        return Utf8Bytes(json) + separators;
    }

    /// <summary>
    /// The size of stored JSON as the administrator's client receives it, which escapes more than storage does. The 8 KiB inspection
    /// budget is measured on this, never on the stored characters. Text that is not valid JSON is reported as over any budget.
    /// </summary>
    internal static int RenderedByteCount(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, RenderedOptions))
            {
                document.RootElement.WriteTo(writer);
            }

            return checked((int)stream.Length);
        }
        catch (JsonException)
        {
            return int.MaxValue;
        }
    }

    internal static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Decimal(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The claim value of a publication date: the date, how much of it was stated, and what it means.</summary>
    internal static string DateValue(DateOnly date, DatePrecision precision, DateBasis basis) =>
        Write(writer =>
        {
            writer.WriteString("date", Date(date));
            writer.WriteString("precision", precision.ToString());
            writer.WriteString("basis", basis.ToString());
        });

    /// <summary>
    /// The claim value of a date the catalog held before any source was attributed. A missing precision or basis is unknown, not a
    /// day or an original: the value carries a placeholder only so that it can be ranked, and says plainly which parts are unknown.
    /// </summary>
    internal static string LegacyDateValue(DateOnly date, DatePrecision? precision, DateBasis? basis) =>
        Write(writer =>
        {
            writer.WriteString("date", Date(date));
            writer.WriteString("precision", (precision ?? DatePrecision.Day).ToString());
            writer.WriteString("basis", (basis ?? DateBasis.Unknown).ToString());
            if (precision is null)
            {
                writer.WriteBoolean("precisionKnown", false);
            }

            if (basis is null)
            {
                writer.WriteBoolean("basisKnown", false);
            }
        });

    /// <summary>
    /// A text claim value. If the value does not fit the 16 KiB byte budget it is shortened to the longest whole-character prefix that
    /// does, and flagged, instead of being rejected.
    /// </summary>
    internal static string TextValue(string name, string rawValue)
    {
        var value = Clean(rawValue);
        var whole = Write(writer => writer.WriteString(name, value));
        if (StoredBytes(whole) <= MetadataClaim.MaximumValueBytes)
        {
            return whole;
        }

        string Shortened(int length) => Write(writer =>
        {
            writer.WriteString(name, value[..length]);
            writer.WriteBoolean("truncated", true);
        });

        // The serialized size never shrinks as the prefix grows, so the longest prefix that fits is found by bisection.
        var low = 0;
        var high = value.Length - 1;
        var best = 0;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var length = WholeCharacters(value, middle);
            if (StoredBytes(Shortened(length)) <= MetadataClaim.MaximumValueBytes)
            {
                best = length;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return Shortened(best);
    }

    internal static string NumberValue(string name, int value) => Write(writer => writer.WriteNumber(name, value));

    /// <summary>The claim value of one source's statement about a series position.</summary>
    internal static string SeriesPositionValue(BookSeriesCandidate series, SeriesOrderingKind ordering) =>
        Write(writer =>
        {
            writer.WriteString("seriesLabel", Truncate(series.Name.Trim(), MaximumText, null));
            WriteNullable(writer, "label", series.PositionLabel, null);
            if (series.PositionSort is { } sort)
            {
                writer.WriteString("sort", Decimal(sort));
            }
            else
            {
                writer.WriteNull("sort");
            }

            writer.WriteString("ordering", ordering.ToString());
            writer.WriteBoolean("featured", series.IsPrimary);
            WriteNullable(writer, "sourceSeriesId", series.SourceSeriesId, null);
        });

    /// <summary>
    /// The normalized snapshot of an observation: what the provider said and what was done with it, never the raw payload. The
    /// description is represented by its hash and length (the claim carries the text), so a snapshot stays small. A snapshot that
    /// does not fit 64 KiB of UTF-8 is rebuilt with fewer list items and shorter strings until it does, and then says so.
    /// </summary>
    internal static (string Json, string Hash) Snapshot(
        BookCandidate candidate,
        EditionProjection projection,
        string outcome,
        string? reason,
        string recordClass,
        IReadOnlyList<string> seriesDispositions)
    {
        foreach (var shape in SnapshotShapes)
        {
            var text = BuildSnapshot(candidate, projection, outcome, reason, recordClass, seriesDispositions, new SnapshotShape(shape));
            if (StoredBytes(text) <= SourceObservation.MaximumSnapshotBytes)
            {
                return (text, Hash(text));
            }
        }

        // Unreachable while every string above is bounded; kept so that no input can ever produce an unstorable snapshot.
        var minimal = Write(writer =>
        {
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteString("outcome", Truncate(outcome, 64, null));
            writer.WriteString("provider", Truncate(candidate.ProviderId, 128, null));
            writer.WriteString("externalId", Truncate(candidate.ExternalId, 128, null));
            writer.WriteBoolean("truncated", true);
        });
        return (minimal, Hash(minimal));
    }

    private static string BuildSnapshot(
        BookCandidate candidate,
        EditionProjection projection,
        string outcome,
        string? reason,
        string recordClass,
        IReadOnlyList<string> seriesDispositions,
        SnapshotShape shape) =>
        Write(writer =>
        {
            var text = shape.Text;
            writer.WriteNumber("schema", SchemaVersion);
            writer.WriteString("outcome", Truncate(outcome, text, shape));
            WriteNullable(writer, "reason", reason, shape);
            writer.WriteString("provider", Truncate(candidate.ProviderId, text, shape));
            writer.WriteString("externalId", Truncate(candidate.ExternalId, text, shape));
            writer.WriteString("title", Truncate(candidate.Title, text, shape));
            WriteNullable(writer, "workTitle", candidate.WorkTitle, shape);
            WriteNullable(writer, "language", CatalogLanguage.Normalize(candidate.Language), shape);
            writer.WriteString("versionClass", Truncate(recordClass, text, shape));
            WriteNullable(writer, "publisher", candidate.Publisher, shape);
            WriteNullable(writer, "coverUrl", candidate.CoverUrl, shape);
            if (candidate.PageCount is { } pages)
            {
                writer.WriteNumber("pageCount", pages);
            }
            else
            {
                writer.WriteNull("pageCount");
            }

            if (string.IsNullOrWhiteSpace(candidate.Description))
            {
                writer.WriteNull("descriptionHash");
            }
            else
            {
                writer.WriteString("descriptionHash", Hash(candidate.Description.Trim()));
                writer.WriteNumber("descriptionLength", candidate.Description.Trim().Length);
            }

            if (candidate.PublicationDate is { } date)
            {
                writer.WriteStartObject("date");
                writer.WriteString("value", Date(date));
                writer.WriteString("precision", (candidate.PublicationDatePrecision ?? DatePrecision.Day).ToString());
                writer.WriteString("basis", (candidate.PublicationDateBasis ?? DateBasis.Unknown).ToString());
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("date");
            }

            writer.WriteStartArray("authors");
            foreach (var name in Limited(candidate.Authors, shape.Credits, shape))
            {
                var credit = candidate.AuthorCredits.FirstOrDefault(item =>
                    CatalogIdentityPolicy.AuthorKey(item.Name) == CatalogIdentityPolicy.AuthorKey(name));
                writer.WriteStartObject();
                writer.WriteString("name", Truncate(name, text, shape));
                WriteNullable(writer, "role", credit?.Role, shape);
                WriteNullable(writer, "sourceId", credit?.SourceAuthorId, shape);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("subjects");
            foreach (var subject in Limited(candidate.Subjects, shape.Subjects, shape))
            {
                writer.WriteStringValue(Truncate(subject, Math.Min(128, text), shape));
            }

            writer.WriteEndArray();

            writer.WriteStartArray("series");
            foreach (var series in Limited(candidate.Series.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(), shape.Series, shape))
            {
                writer.WriteStartObject();
                writer.WriteString("name", Truncate(series.Name, text, shape));
                WriteNullable(writer, "label", series.PositionLabel, shape);
                if (series.PositionSort is { } sort)
                {
                    writer.WriteString("sort", Decimal(sort));
                }
                else
                {
                    writer.WriteNull("sort");
                }

                writer.WriteBoolean("featured", series.IsPrimary);
                writer.WriteBoolean("completed", series.IsCompleted);
                writer.WriteString("ordering", SeriesOrdering.Of(series).ToString());
                WriteNullable(writer, "sourceSeriesId", series.SourceSeriesId, shape);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("seriesBinding");
            foreach (var disposition in Limited(seriesDispositions, shape.Series, shape))
            {
                writer.WriteStringValue(Truncate(disposition, text, shape));
            }

            writer.WriteEndArray();

            WriteEditions(writer, "editions", projection.Compatible, shape);
            WriteEditions(writer, "unattachedEditions", projection.Excluded, shape);

            if (shape.Cut)
            {
                writer.WriteBoolean("truncated", true);
            }
        });

    private static void WriteEditions(Utf8JsonWriter writer, string name, IReadOnlyList<ProjectedEdition> editions, SnapshotShape shape)
    {
        writer.WriteStartArray(name);
        foreach (var projected in Limited(
                     editions
                         .OrderBy(item => item.Isbn13 ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(item => item.Edition.Title, StringComparer.Ordinal)
                         .ThenBy(item => item.Edition.Format, StringComparer.Ordinal)
                         .ToArray(),
                     shape.Editions,
                     shape))
        {
            var edition = projected.Edition;
            writer.WriteStartObject();
            writer.WriteString("title", Truncate(edition.Title ?? string.Empty, shape.Text, shape));
            WriteNullable(writer, "isbn13", projected.Isbn13, shape);
            writer.WriteString("format", Truncate(edition.Format ?? string.Empty, Math.Min(64, shape.Text), shape));
            if (edition.PublicationDate is { } date)
            {
                writer.WriteString("date", Date(date));
                writer.WriteString("precision", (edition.PublicationDatePrecision ?? DatePrecision.Day).ToString());
            }
            else
            {
                writer.WriteNull("date");
            }

            WriteNullable(writer, "language", CatalogLanguage.Normalize(edition.Language), shape);
            WriteNullable(writer, "publisher", edition.Publisher, shape);
            if (projected.Exclusion is { } exclusion)
            {
                writer.WriteString("exclusion", exclusion.ToString());
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>The first <paramref name="limit"/> items; dropping any marks the snapshot as shortened.</summary>
    private static IEnumerable<T> Limited<T>(IReadOnlyList<T> items, int limit, SnapshotShape shape)
    {
        if (items.Count > limit)
        {
            shape.Cut = true;
        }

        return items.Take(limit);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value, SnapshotShape? shape)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, Truncate(value.Trim(), shape?.Text ?? MaximumText, shape));
        }
    }

    /// <summary>
    /// Bounds a string to a number of whole characters and cleans it: PostgreSQL's jsonb cannot store a NUL, and a stray NUL or lone
    /// surrogate in a provider's text must never fail an observation. A string that had to be shortened marks the snapshot.
    /// </summary>
    private static string Truncate(string value, int length, SnapshotShape? shape)
    {
        var clean = Clean(value);
        if (clean.Length <= length)
        {
            return clean;
        }

        if (shape is not null)
        {
            shape.Cut = true;
        }

        return clean[..WholeCharacters(clean, length)];
    }

    /// <summary>Removes NULs and replaces a lone surrogate (which no UTF-8 text can hold) with the replacement character.</summary>
    private static string Clean(string value)
    {
        StringBuilder? cleaned = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\0')
            {
                cleaned ??= new StringBuilder(value, 0, index, value.Length);
                continue;
            }

            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                cleaned?.Append(character).Append(value[index + 1]);
                index++;
                continue;
            }

            if (char.IsSurrogate(character))
            {
                cleaned ??= new StringBuilder(value, 0, index, value.Length);
                cleaned.Append('�');
                continue;
            }

            cleaned?.Append(character);
        }

        return cleaned?.ToString() ?? value;
    }

    /// <summary>The longest length at or below <paramref name="length"/> that does not end between the two halves of a surrogate pair.</summary>
    private static int WholeCharacters(string value, int length) =>
        length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]) ? length - 1 : length;

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>One reduction level of a snapshot, and whether anything was cut while building it.</summary>
    private sealed class SnapshotShape(int editions, int credits, int subjects, int series, int text)
    {
        public SnapshotShape(SnapshotShape template)
            : this(template.Editions, template.Credits, template.Subjects, template.Series, template.Text)
        {
        }

        public int Editions { get; } = editions;

        public int Credits { get; } = credits;

        public int Subjects { get; } = subjects;

        public int Series { get; } = series;

        public int Text { get; } = text;

        public bool Cut { get; set; }
    }
}
