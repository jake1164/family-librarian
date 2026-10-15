using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Projects a finished <see cref="CandidateIdentityAssessment"/> into stable, citable
/// <see cref="EvidenceFinding"/>s.
/// </summary>
/// <remarks>
/// Purely descriptive: it reads the already-decided assessment and never feeds back
/// into it, so adding or changing a finding cannot change a matching decision.
/// Ids are deterministic (<c>ev-title</c>, <c>ev-author</c>, <c>ev-language</c>,
/// <c>ev-series-N</c>, <c>ev-contradiction-*</c>, <c>ev-condition-N</c>, <c>ev-decision</c>);
/// codes come from <see cref="EvidenceReasonCodes"/>.
/// </remarks>
public static class CandidateEvidenceFindings
{
    public static IReadOnlyList<EvidenceFinding> Build(CandidateIdentityAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var findings = new List<EvidenceFinding>
        {
            new("ev-title", Code("title", assessment.TitleEvidence.State.ToString()), Polarity(assessment.TitleEvidence.State),
                assessment.TitleEvidence.Reason),
            new("ev-author", Code("author", assessment.AuthorEvidence.Kind.ToString()), Polarity(assessment.AuthorEvidence.Kind),
                $"Author evidence: {assessment.AuthorEvidence.Kind}."),
            new("ev-language", Code("language", assessment.LanguageEvidence.ToString()), Polarity(assessment.LanguageEvidence),
                assessment.LanguageEvidence == IdentityEvidenceState.Unknown
                    ? "Language not stated; unknown is not a contradiction."
                    : $"Language evidence: {assessment.LanguageEvidence}.")
        };

        var seriesIndex = 0;
        foreach (var series in assessment.SeriesEvidence)
        {
            seriesIndex++;
            findings.Add(new($"ev-series-{seriesIndex}", Code("series", series.State.ToString()), Polarity(series.State), series.Reason));
        }

        // Contradictions are derived from the same evidence states the resolver used to
        // build the human-readable list, so the two always agree.
        if (assessment.TitleEvidence.State == IdentityEvidenceState.Conflicting)
            findings.Add(new("ev-contradiction-title", EvidenceReasonCodes.CandidateContradiction.Title, EvidencePolarity.Negative,
                assessment.TitleEvidence.Reason));
        if (assessment.AuthorEvidence.Kind == AuthorAffinityKind.Conflict)
            findings.Add(new("ev-contradiction-author", EvidenceReasonCodes.CandidateContradiction.Author, EvidencePolarity.Negative,
                $"Author conflicts: requested {assessment.RequestedMetadata.Author}, observed {assessment.AuthorEvidence.DetectedAuthor}."));
        var conflictIndex = 0;
        foreach (var series in assessment.SeriesEvidence.Where(value => value.State == IdentityEvidenceState.Conflicting))
        {
            conflictIndex++;
            findings.Add(new($"ev-contradiction-series-{conflictIndex}", EvidenceReasonCodes.CandidateContradiction.SeriesPosition,
                EvidencePolarity.Negative,
                $"Series {series.Name} position conflicts: requested {series.ExpectedPosition}, observed {series.ObservedPosition}."));
        }

        if (assessment.LanguageEvidence == IdentityEvidenceState.Conflicting)
            findings.Add(new("ev-contradiction-language", EvidenceReasonCodes.CandidateContradiction.Language, EvidencePolarity.Negative,
                "Language conflicts with the request."));

        var conditionIndex = 0;
        foreach (var condition in assessment.Conditions)
        {
            conditionIndex++;
            findings.Add(new($"ev-condition-{conditionIndex}", condition.Code ?? GenericConditionCode(condition.Kind),
                EvidencePolarity.Condition, condition.Reason));
        }

        if (assessment.Decision == WorkIdentityDecision.Ambiguous && assessment.Contradictions.Count == 0)
            findings.Add(new("ev-decision", EvidenceReasonCodes.CandidateDecision.InsufficientEvidence, EvidencePolarity.Unknown,
                "The evidence does not establish that this release is the requested work."));

        return findings;
    }

    private static string GenericConditionCode(ReleaseConditionKind kind) => kind switch
    {
        ReleaseConditionKind.CompanionParts => EvidenceReasonCodes.CandidateCondition.CompanionParts,
        ReleaseConditionKind.MalformedStructure => EvidenceReasonCodes.CandidateCondition.MalformedStructure,
        _ => EvidenceReasonCodes.CandidateCondition.EditionReview
    };

    /// <summary>Builds <c>candidate.&lt;facet&gt;.&lt;snake_case_value&gt;</c> from an evidence enum name.</summary>
    public static string Code(string facet, string value) =>
        $"candidate.{facet}.{Regex.Replace(value, "(?<=[a-z])(?=[A-Z])", "_", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).ToLowerInvariant()}";

    private static EvidencePolarity Polarity(IdentityEvidenceState state) => state switch
    {
        IdentityEvidenceState.Exact or IdentityEvidenceState.Compatible => EvidencePolarity.Positive,
        IdentityEvidenceState.Partial or IdentityEvidenceState.Fuzzy => EvidencePolarity.Weak,
        IdentityEvidenceState.Conflicting => EvidencePolarity.Negative,
        _ => EvidencePolarity.Unknown
    };

    private static EvidencePolarity Polarity(AuthorAffinityKind kind) => kind switch
    {
        AuthorAffinityKind.Exact or AuthorAffinityKind.Compatible or AuthorAffinityKind.LastExactFirstFuzzy
            or AuthorAffinityKind.FirstExactLastFuzzy => EvidencePolarity.Positive,
        AuthorAffinityKind.Fuzzy or AuthorAffinityKind.FirstOnly or AuthorAffinityKind.LastOnly
            or AuthorAffinityKind.ContextCorroborated => EvidencePolarity.Weak,
        AuthorAffinityKind.Conflict => EvidencePolarity.Negative,
        _ => EvidencePolarity.Unknown
    };
}
