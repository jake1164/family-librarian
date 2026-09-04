using System.Globalization;

namespace FamilyLibrarian.Infrastructure.Metadata;

// Providers disagree on language code format: Open Library returns ISO 639-2
// three-letter codes (e.g. "eng"), Google Books returns ISO 639-1 two-letter
// codes (e.g. "en"), sometimes with a region suffix (e.g. "en-US"). Normalizing
// to two-letter codes here lets ranking logic compare candidates from either
// provider without knowing where they came from.
internal static class LanguageCodeNormalizer
{
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
            3 => ThreeToTwoLetterCodes.TryGetValue(primary, out var twoLetter)
                ? twoLetter
                : primary.ToLowerInvariant(),
            _ => null
        };
    }
}
