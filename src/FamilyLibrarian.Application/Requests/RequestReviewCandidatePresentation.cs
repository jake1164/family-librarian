using System.Globalization;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Requests;

/// <summary>
/// Converts existing provider-search evidence into a short, neutral description
/// for a requester choosing among possible editions. This deliberately excludes
/// source URLs, provider identities, raw release names, and extension payloads.
/// </summary>
public static class RequestReviewCandidatePresentation
{
    public static string? BuildDetails(FulfillmentOption option)
    {
        var facts = new List<string>();

        var format = NormalizeFormat(option.Format);
        if (format is not null)
        {
            facts.Add(option.MediaType == RequestMediaType.Audiobook
                ? $"{format} audiobook".ToUpperInvariant()
                : format.ToUpperInvariant());
        }

        var narration = DescribeNarration(option);
        if (narration is not null)
        {
            facts.Add(narration);
        }

        if (option.PublicationYear is >= 1000 and <= 9999)
        {
            facts.Add($"Published {option.PublicationYear.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var publisher = CleanBibliographicFact(option.Publisher, 120);
        if (publisher is not null)
        {
            facts.Add(publisher);
        }

        if (option.SizeBytes is > 0)
        {
            facts.Add(FormatSize(option.SizeBytes.Value));
        }

        if (option.PartCount is > 1)
        {
            facts.Add($"{option.PartCount.Value.ToString(CultureInfo.InvariantCulture)} parts");
        }

        if (option.ProviderPopularity is > 0)
        {
            facts.Add($"{option.ProviderPopularity.Value.ToString("N0", CultureInfo.InvariantCulture)} source downloads");
        }

        if (option.IsUnabridged == true)
        {
            facts.Add("Unabridged");
        }
        else if (option.IsAbridged == true)
        {
            facts.Add("Abridged");
        }

        return facts.Count == 0 ? null : string.Join(" · ", facts);
    }

    /// <summary>
    /// Narration is meaningful audiobook evidence, not a quality score --
    /// shown by kind, never ranked or colored (docs/07-ui-conventions.md).
    /// Absent for every non-audiobook option and whenever the provider's own
    /// evidence did not support a confident classification.
    /// </summary>
    private static string? DescribeNarration(FulfillmentOption option) => option.NarrationKind switch
    {
        NarrationKind.Human => string.IsNullOrWhiteSpace(option.Narrator)
            ? "Human narration"
            : $"Human narration — {Clean(option.Narrator, 120)}",
        NarrationKind.Synthetic => "Computer-generated narration",
        _ => null
    };

    private static string? NormalizeFormat(string? value) => Clean(value, 40)?.ToLowerInvariant() switch
    {
        "epub" => "EPUB",
        "pdf" => "PDF",
        "mobi" => "MOBI",
        "azw" => "AZW",
        "azw3" => "AZW3",
        "fb2" => "FB2",
        "fbz" => "FBZ",
        "kepub" => "KEPUB",
        "prc" => "PRC",
        "docx" => "DOCX",
        "cbz" => "CBZ",
        "cbr" => "CBR",
        "mp3" => "MP3",
        "m4a" => "M4A",
        "m4b" => "M4B",
        "aac" => "AAC",
        "ogg" => "OGG",
        "opus" => "OPUS",
        "flac" => "FLAC",
        _ => null
    };

    private static string? CleanBibliographicFact(string? value, int maxLength)
    {
        var cleaned = Clean(value, maxLength);
        return cleaned is null || LooksLikeSourceReference(cleaned) ? null : cleaned;
    }

    private static bool LooksLikeSourceReference(string value) =>
        value.Contains("://", StringComparison.Ordinal) ||
        value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
        Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd() + "…";
    }

    private static string FormatSize(long sizeBytes)
    {
        const long kibibyte = 1024;
        const long mebibyte = kibibyte * 1024;
        const long gibibyte = mebibyte * 1024;

        return sizeBytes switch
        {
            >= gibibyte => $"{sizeBytes / (double)gibibyte:0.#} GB",
            >= mebibyte => $"{sizeBytes / (double)mebibyte:0.#} MB",
            >= kibibyte => $"{sizeBytes / (double)kibibyte:0.#} KB",
            _ => $"{sizeBytes.ToString(CultureInfo.InvariantCulture)} bytes"
        };
    }
}
