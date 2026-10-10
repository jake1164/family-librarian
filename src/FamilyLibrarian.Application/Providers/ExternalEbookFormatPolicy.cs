namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// The formats Family Librarian may obtain as ebook conversion sources from an
/// external provider. This is an acquisition policy, not a statement that an
/// arbitrary file bearing one of these extensions is safe: staging still
/// validates bytes and malware-scans every acquired artifact.
/// </summary>
public static class ExternalEbookFormatPolicy
{
    private static readonly HashSet<string> SafeFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "epub", "azw3", "mobi", "azw"
    };

    private static readonly HashSet<string> PossibleFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "fb2", "fbz", "kepub", "prc", "docx"
    };

    /// <summary>
    /// The provider-side search hint for ebook requests. A provider may ignore
    /// it, so <see cref="Classify"/> remains the enforcement point.
    /// </summary>
    public static readonly IReadOnlyList<string> SearchFormats =
        ["epub", "azw3", "mobi", "azw", "fb2", "fbz", "kepub", "prc", "docx"];

    public static ExternalEbookFormatTier Classify(string? format)
    {
        var normalized = Normalize(format);
        if (normalized is null)
        {
            return ExternalEbookFormatTier.Reject;
        }

        if (SafeFormats.Contains(normalized))
        {
            return ExternalEbookFormatTier.Safe;
        }

        return PossibleFormats.Contains(normalized)
            ? ExternalEbookFormatTier.Possible
            : ExternalEbookFormatTier.Reject;
    }

    /// <summary>Lower is better for selecting one otherwise-equivalent Safe source.</summary>
    public static int AcquisitionPreference(string? format) => Normalize(format) switch
    {
        "epub" => 0,
        "azw3" or "mobi" => 1,
        "azw" => 2,
        _ => 3
    };

    private static string? Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        return format.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "azw1" => "azw",
            "azw3" => "azw3",
            var value when value.Length > 0 => value,
            _ => null
        };
    }
}

public enum ExternalEbookFormatTier
{
    Reject,
    Possible,
    Safe
}
