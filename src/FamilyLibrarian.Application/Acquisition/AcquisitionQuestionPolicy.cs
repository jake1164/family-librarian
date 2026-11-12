using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Evidence;
using FamilyLibrarian.Domain.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

public sealed record AcquisitionQuestionOffer(AcquisitionQuestionKind Kind, FulfillmentOption Candidate, string? SearchVariant = null);

/// <summary>Product choices only. This never changes the candidates eligible for automatic downloading.</summary>
public static class AcquisitionQuestionPolicy
{
    public static AcquisitionQuestionOffer? Select(BookIdentity requested, IReadOnlyList<FulfillmentOption> options,
        RequestMediaType mediaType, ProviderConfidenceLevel level)
    {
        if (options.Any(o => CandidateConfidence.Allows(level, CandidateConfidence.Classify(o)))) return null;
        var classified = options.Select(o => (Option: o, Kind: KindOf(requested, o, level))).ToArray();
        var pool = classified.Where(item => item.Kind is not null ||
            item.Option.AcquisitionAssessment?.Suitability != AcquisitionSuitability.Blocked &&
            LanguageAcceptance.IsAcceptedOrUnspecified(item.Option.Language, requested.Language)).ToArray();
        var ranked = CandidateConfidence.Rank(pool.Select(item => item.Option), mediaType);
        var best = ranked.Count == 0 ? null : ranked[0];
        if (best is null) return null;
        var kind = classified.First(item => ReferenceEquals(item.Option, best)).Kind;
        if (kind == AcquisitionQuestionKind.AbridgedOnly && pool.Any(item => item.Kind != AcquisitionQuestionKind.AbridgedOnly)) return null;
        if (kind == AcquisitionQuestionKind.OtherLanguageOnly && options.Any(o => o.HasPlausibleTitle &&
            KindOf(requested, o, level) != AcquisitionQuestionKind.OtherLanguageOnly)) return null;
        if (kind == AcquisitionQuestionKind.PartsOnly && AudiobookPartSetSelector.TrySelect(options) is not null) return null;
        return kind is { } value ? new(value, best) : null;
    }

    public static AcquisitionQuestionKind? KindOf(BookIdentity requested, FulfillmentOption option, ProviderConfidenceLevel level)
    {
        if (option.IdentityAssessment is not { } assessment || assessment.AuthorEvidence.Kind == AuthorAffinityKind.Conflict) return null;
        var source = assessment.SourceMetadata;
        var candidate = new ExternalProviderCandidate(source.ProviderReference, source.Work, source.Edition, source.Release);
        var codes = assessment.Conditions.Select(c => c.Code).Distinct().ToArray();
        var qualifiers = (assessment.ReleaseEvidence.StructuralQualifiers ?? [])
            .Concat(ExternalReleaseNameEvidence.Evaluate(source.Work.Title, [requested.Title], requested.Author, requested.Series).StructuralQualifiers ?? []).ToArray();
        AcquisitionQuestionKind? kind = null;
        if ((codes.Any(c => c is EvidenceReasonCodes.CandidateCondition.Collection or EvidenceReasonCodes.CandidateCondition.CombinedWork) ||
             qualifiers.Any(IsCollectionQualifier)) &&
            codes.All(c => c is EvidenceReasonCodes.CandidateCondition.Collection or EvidenceReasonCodes.CandidateCondition.CombinedWork ||
                c == EvidenceReasonCodes.CandidateCondition.QualifierReview && qualifiers.All(IsCollectionQualifier)) &&
            CollectionContainment.Evaluate(requested, candidate) == ContainmentVerdict.Contains)
        {
            kind = AcquisitionQuestionKind.CollectionContaining;
            candidate = candidate with { Work = candidate.Work with { Title = requested.Title },
                Release = candidate.Release is null ? null : candidate.Release with { Name = null, IsCollection = false } };
        }
        else if (assessment.LanguageEvidence == IdentityEvidenceState.Conflicting && !string.IsNullOrWhiteSpace(option.Language) && codes.Length == 0 &&
                 assessment.Findings.Where(f => f.Polarity == EvidencePolarity.Negative).All(f =>
                     f.Code is EvidenceReasonCodes.CandidateContradiction.Language or EvidenceReasonCodes.CandidateLanguage.Conflicting))
        {
            kind = AcquisitionQuestionKind.OtherLanguageOnly;
            requested = requested with { Language = option.Language };
        }
        else if (assessment.TitleEvidence.IsPositive && assessment.Contradictions.Count == 0 &&
                 (codes.Contains(EvidenceReasonCodes.CandidateCondition.Abridged) || qualifiers.Contains("ABRIDGED", StringComparer.Ordinal)) &&
                 codes.All(c => c == EvidenceReasonCodes.CandidateCondition.Abridged ||
                    c == EvidenceReasonCodes.CandidateCondition.QualifierReview && qualifiers.All(q => q == "ABRIDGED")))
        {
            kind = AcquisitionQuestionKind.AbridgedOnly;
            candidate = candidate with { Work = candidate.Work with { Title = requested.Title },
                Release = candidate.Release is null ? null : candidate.Release with { Name = null, IsAbridged = false } };
        }
        else if (option.FragmentOnlyConcern && codes.All(c => c == EvidenceReasonCodes.CandidateCondition.CompanionParts))
            kind = AcquisitionQuestionKind.PartsOnly;
        if (kind is null) return null;
        var proof = DeterministicCandidateIdentityResolver.Assess(requested, candidate);
        if (!proof.HasStrongTitle || proof.Decision == WorkIdentityDecision.Mismatch) return null;
        var confidence = option with
        {
            RequiresReleaseConfirmation = false, RequiresLanguageConfirmation = false,
            AcquisitionAssessment = null, IdentityAssessment = proof,
            MatchBasis = proof.AuthorEvidence.HasStrongSupport ? BookMatchBasis.StrictTitleAuthor : BookMatchBasis.StrictTitle,
            AuthorAffinity = proof.AuthorEvidence
        };
        return CandidateConfidence.Allows(level, CandidateConfidence.Classify(confidence)) ? kind : null;
    }

    private static bool IsCollectionQualifier(string value)
    {
        if (value is "OMNIBUS" or "BOX SET" or "BOXED SET" or "COLLECTION" or "STORIES") return true;
        var tokens = ReleaseTitleMatcher.Tokens(value);
        return tokens.Length == 3 && tokens[0] == "BOOKS" && tokens.Skip(1).All(t => t.All(char.IsDigit));
    }

    public static string Text(AcquisitionQuestionKind kind, string work, string candidate, string? language = null) => kind switch
    {
        AcquisitionQuestionKind.CollectionContaining => $"I only found {candidate}, which includes {work}. Take it, or keep looking for a standalone copy?",
        AcquisitionQuestionKind.AbridgedOnly => $"I only found an abridged version of {work}, which is shortened. Take it, or keep looking for an unabridged copy?",
        AcquisitionQuestionKind.OtherLanguageOnly => $"I only found {candidate} in {LanguageLabel(language)}. Take it, or keep looking for a copy in your requested language?",
        AcquisitionQuestionKind.PartsOnly => $"I only found part of {work}, without a complete set. Take it, or keep looking for a complete copy?",
        AcquisitionQuestionKind.DramatizedOnly => $"I only found a dramatized version of {work}: a full-cast production with several actors, music and sound effects, not the narrated audiobook. Take it, or keep looking for a narrated copy?",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string LanguageLabel(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "another language";
        try { return System.Globalization.CultureInfo.GetCultureInfo(language.Trim()).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return language.Trim(); }
    }
}
