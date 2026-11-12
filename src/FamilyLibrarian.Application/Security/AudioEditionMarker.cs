using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Security;

public sealed record AudioEditionMarker(string CoreTitle, string? Marker, bool IsAbridged, bool IsGerman)
{
    /// <summary>Exactly one terminal marker; never removes arbitrary subtitles or internal words.</summary>
    public static AudioEditionMarker Strip(string title)
    {
        var trimmed = title.TrimEnd();
        foreach (var marker in new[] { "Unabridged", "Abridged", "ungekürzt", "gekürzt" })
        {
            foreach (var suffix in new[] { $"({marker})", $"[{marker}]", $"- {marker}" })
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return new(trimmed[..^suffix.Length].TrimEnd(), marker, marker is "Abridged" or "gekürzt",
                        marker is "ungekürzt" or "gekürzt");
            }
        }
        return new(title, null, false, false);
    }

    public IReadOnlyList<IdentityFact> Facts(string? acceptedLanguage)
    {
        var facts = new List<IdentityFact>();
        if (IsAbridged)
            facts.Add(new("ev-audio-abridged", EvidenceDimension.Profile, EvidenceReasonCodes.ProfileComparison.AbridgedMarker,
                EvidencePolarity.Condition, Marker));
        if (IsGerman)
        {
            var agrees = LanguageAcceptance.IsAcceptedOrUnspecified("de", acceptedLanguage);
            facts.Add(new("ev-language-marker", EvidenceDimension.Language, agrees ? EvidenceReasonCodes.ArtifactLanguage.CueMatchesRequest
                : EvidenceReasonCodes.ArtifactLanguage.CueConflicts, agrees ? EvidencePolarity.Positive : EvidencePolarity.Negative, $"cue:{Marker?.ToUpperInvariant()}"));
        }
        return facts;
    }
}
