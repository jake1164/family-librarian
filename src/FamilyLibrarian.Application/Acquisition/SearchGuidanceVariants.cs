using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.SemanticAssessment;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Reconstructs search proposals only. The query never becomes identity or permission evidence.</summary>
public static class SearchGuidanceVariants
{
    public static IReadOnlyList<SearchVariant>? Build(BookIdentity requested, SearchGuidance guidance)
    {
        var variants = new List<SearchVariant>();
        foreach (var suggestion in guidance.Suggestions)
        {
            var variant = BuildOne(requested, suggestion.Reason, suggestion.Value);
            if (variant is null) return null;
            variants.Add(variant);
        }
        return variants;
    }

    /// <summary>Replays a server-stored key before acquiring or asking about the candidate it found.</summary>
    public static SearchVariant? Restore(BookIdentity requested, SearchVariantRef variant)
    {
        if (variant.Kind != SearchVariantKind.AiSuggestion) return null;
        var colon = variant.Key.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? null : BuildOne(requested, variant.Key[..colon], variant.Key[(colon + 1)..]);
    }

    private static SearchVariant? BuildOne(BookIdentity requested, string reason, string value)
    {
        if (!SemanticAssessmentParser.IsSearchValue(value)) return null;
        var baseVariant = SearchVariantPlanner.BaseVariant(requested);
        var work = baseVariant.Work;
        switch (reason)
        {
            case "original_language_title":
            case "alternate_published_title":
            case "contained_in":
                work = work with { Title = value };
                break;
            case "author_variant":
                work = work with { Authors = [new BookAuthor(value, "author")] };
                break;
            case "series_naming":
                var positions = (requested.Series ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Position))
                    .Select(s => s.Position!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
                // No position may be invented. Ambiguous positions reject the whole reply before any search is run.
                if (positions.Length != 1) return null;
                work = work with { Title = $"{value} {positions[0]}" };
                break;
            default: return null;
        }
        return new(SearchVariantKind.AiSuggestion, $"{reason}:{SearchVariantPlanner.Normalize(value)}", work, baseVariant.Edition);
    }
}
