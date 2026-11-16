using System.Globalization;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Normalizes the language codes providers report to the two-letter form the catalog compares: Open Library reports ISO 639-2
/// three-letter codes ("eng"), Google Books ISO 639-1 codes sometimes with a region ("en-US"). An unrecognized three-letter code
/// is kept as is; anything else is unknown (null), never guessed.
/// </summary>
public static class CatalogLanguage
{
    /// <summary>The language the catalog has always assumed for a Work whose editions state none.</summary>
    public const string Default = "en";

    private static readonly Dictionary<string, string> ThreeToTwoLetterCodes =
        CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(culture =>
                culture.ThreeLetterISOLanguageName.Length == 3 &&
                culture.TwoLetterISOLanguageName.Length == 2)
            .GroupBy(culture => culture.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().TwoLetterISOLanguageName.ToLowerInvariant(),
                StringComparer.OrdinalIgnoreCase);

    // Open Library reports MARC/ISO 639-2 "bibliographic" codes, which differ from the "terminologic" codes .NET knows for these
    // twenty languages ("ger", not "deu"). Without this a German edition would not compare equal to a German record.
    private static readonly Dictionary<string, string> BibliographicCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu", ["bur"] = "my", ["chi"] = "zh", ["cze"] = "cs", ["dut"] = "nl",
        ["fre"] = "fr", ["geo"] = "ka", ["ger"] = "de", ["gre"] = "el", ["ice"] = "is", ["mac"] = "mk", ["mao"] = "mi",
        ["may"] = "ms", ["per"] = "fa", ["rum"] = "ro", ["slo"] = "sk", ["tib"] = "bo", ["wel"] = "cy"
    };

    public static string? Normalize(string? code)
    {
        var primary = code?.Trim().Split('-', '_')[0];
        if (string.IsNullOrEmpty(primary))
        {
            return null;
        }

        return primary.Length switch
        {
            2 => primary.ToLowerInvariant(),
            3 => BibliographicCodes.TryGetValue(primary, out var bibliographic)
                ? bibliographic
                : ThreeToTwoLetterCodes.TryGetValue(primary, out var twoLetter)
                    ? twoLetter
                    : primary.ToLowerInvariant(),
            _ => null
        };
    }
}
