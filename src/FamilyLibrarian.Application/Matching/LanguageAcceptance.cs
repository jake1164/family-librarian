using System.Globalization;

namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// The English-default language policy for automatic matching
/// (ACCURACY-1: English-language enforcement in matching). Full
/// multi-language/household-preference support is a longer-term goal, not
/// this.
/// </summary>
public static class LanguageAcceptance
{
    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    /// <summary>
    /// True when <paramref name="language"/> is missing (treated as
    /// unspecified, not rejected -- rejecting on missing metadata would hurt
    /// automation more than it helps accuracy) or declares English in any of
    /// its common forms (<c>en</c>, <c>eng</c>, <c>en-US</c>, <c>English</c>).
    /// </summary>
    public static bool IsEnglishOrUnspecified(string? language) => IsAcceptedOrUnspecified(language, null);

    /// <summary>
    /// Compares declared language with the accepted language (English by default).
    /// Known ISO codes, regional tags and English language names are normalized.
    /// Unrecognized values must match exactly; they never disable the check.
    /// </summary>
    public static bool IsAcceptedOrUnspecified(string? language, string? acceptedLanguage) =>
        string.IsNullOrWhiteSpace(language) ||
        string.Equals(Normalize(language), Normalize(string.IsNullOrWhiteSpace(acceptedLanguage)
            ? "en" : acceptedLanguage), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string language)
    {
        var value = language.Trim();
        if (Aliases.TryGetValue(value, out var normalized))
        {
            return normalized;
        }

        // Compare language, not region. Only a known ISO language subtag may
        // be shortened, so values such as "English-ish" cannot become English.
        var separator = value.IndexOfAny(['-', '_']);
        if (separator is 2 or 3 && Aliases.TryGetValue(value[..separator], out normalized))
        {
            return normalized;
        }

        return value;
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.Name.Length == 0)
            {
                continue;
            }

            var language = culture.TwoLetterISOLanguageName;
            aliases[culture.Name] = language;
            aliases[language] = language;
            aliases[culture.ThreeLetterISOLanguageName] = language;
            aliases[culture.EnglishName] = language;
        }

        // ISO 639-2 bibliographic aliases used by library catalogs, alongside
        // the terminology codes supplied by .NET (fra, deu, zho, ...).
        foreach (var (bibliographic, language) in new (string, string)[]
        {
            ("alb", "sq"), ("arm", "hy"), ("baq", "eu"), ("bur", "my"), ("chi", "zh"),
            ("cze", "cs"), ("dut", "nl"), ("fre", "fr"), ("geo", "ka"), ("ger", "de"),
            ("gre", "el"), ("ice", "is"), ("mac", "mk"), ("mao", "mi"), ("may", "ms"),
            ("per", "fa"), ("rum", "ro"), ("slo", "sk"), ("tib", "bo"), ("wel", "cy")
        })
        {
            aliases[bibliographic] = language;
        }

        return aliases;
    }
}
