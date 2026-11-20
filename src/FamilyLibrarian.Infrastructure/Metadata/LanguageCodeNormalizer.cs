using FamilyLibrarian.Application.Catalog;

namespace FamilyLibrarian.Infrastructure.Metadata;

// Providers disagree on language code format: Open Library returns ISO 639-2 three-letter codes (e.g. "eng"), Google Books returns
// ISO 639-1 two-letter codes (e.g. "en"), sometimes with a region suffix (e.g. "en-US"). The normalization itself lives with the
// catalog policy that compares the result (CatalogLanguage), so a provider mapper and the identity rules cannot disagree on it.
internal static class LanguageCodeNormalizer
{
    public static string? Normalize(string? code) => CatalogLanguage.Normalize(code);
}
