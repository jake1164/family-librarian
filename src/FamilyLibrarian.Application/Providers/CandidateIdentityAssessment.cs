using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Providers;

/// <summary>Server-side evidence for classification and future semantic resolution. Unknown is not conflict.</summary>
public sealed record CandidateSourceMetadata(
    string ProviderReference, ExternalProviderWorkEvidence Work, ExternalProviderEditionEvidence? Edition,
    ExternalProviderReleaseEvidence? Release, string? ExtensionsJson)
{
    public string Title => Work.Title;
}

public sealed record CandidateIdentityAssessment(
    BookIdentity RequestedMetadata, CandidateSourceMetadata SourceMetadata,
    ReleaseNameVerdict ReleaseEvidence, ReleaseTitleEvidence TitleEvidence,
    AuthorAffinityResult AuthorEvidence, IReadOnlyList<SeriesIdentityEvidence> SeriesEvidence,
    IdentityEvidenceState LanguageEvidence, IReadOnlyList<string> Contradictions,
    IReadOnlyList<ReleaseCondition> Conditions, WorkIdentityDecision Decision, IReadOnlyList<string> Reasons)
{
    public WorkIdentityDecision WorkIdentity => Decision == WorkIdentityDecision.MatchWithConditions
        ? WorkIdentityDecision.Match : Decision;

    public bool HasStrongTitle => TitleEvidence.IsPositive &&
        (ReleaseTitleMatcher.Tokens(TitleEvidence.Expected).Sum(token => token.Length) > 3 || AuthorEvidence.HasStrongSupport) &&
        (TitleEvidence.State != IdentityEvidenceState.Fuzzy || AuthorEvidence.HasStrongSupport) &&
        (string.IsNullOrWhiteSpace(SourceMetadata.Title) ||
         ReleaseTitleMatcher.Tokens(SourceMetadata.Title).Length == TitleEvidence.Length ||
         HasExactStructuredAuthorSuffix());

    private bool HasExactStructuredAuthorSuffix()
    {
        var marker = SourceMetadata.Title.LastIndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        return marker > 0 && TitleEvidence.Start == 0 &&
            ReleaseTitleMatcher.Tokens(SourceMetadata.Title[..marker]).Length == TitleEvidence.Length &&
            AuthorAffinity.Evaluate(RequestedMetadata.Author, SourceMetadata.Title[(marker + 4)..]).HasStrongSupport;
    }
}

/// <summary>Extraction and deterministic classification only; it cannot authorize acquisition.</summary>
public static class DeterministicCandidateIdentityResolver
{
    public static CandidateIdentityAssessment Assess(BookIdentity requested, ExternalProviderCandidate source)
    {
        var requestedTitles = new[] { requested.Title }.Concat(requested.AlternateTitles ?? []).ToArray();
        var titles = requestedTitles.Concat(requestedTitles.Select(value => WorkTitleCore.Reduce(value, requested.Author)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var release = ExternalReleaseNameEvidence.Evaluate(source.Release?.Name, titles, requested.Author, requested.Series);
        var title = string.IsNullOrWhiteSpace(source.Title) ? release.TitleEvidence : titles
            .Select(expected => ReleaseTitleMatcher.Evaluate(expected, ReleaseTitleMatcher.Tokens(source.Title)))
            .OrderBy(evidence => evidence.State switch { IdentityEvidenceState.Exact => 0, IdentityEvidenceState.Compatible => 1, IdentityEvidenceState.Fuzzy => 2, _ => 3 }).FirstOrDefault();
        title ??= new(IdentityEvidenceState.Unknown, requested.Title, null, -1, 0, "No title or release name supplied.");
        if (!string.IsNullOrWhiteSpace(source.Title) && !title.IsPositive)
            title = title with { State = IdentityEvidenceState.Conflicting, Observed = source.Title, Reason = "Structured source title does not identify the requested work." };
        var structuredName = string.IsNullOrWhiteSpace(source.Title) ? ReleaseNameVerdict.None :
            ExternalReleaseNameEvidence.Evaluate(source.Title, titles, requested.Author, requested.Series);
        var structuredAuthors = source.Work.Authors.Where(person => person.Role is null ||
            person.Role.Equals("author", StringComparison.OrdinalIgnoreCase) || person.Role.Equals("coauthor", StringComparison.OrdinalIgnoreCase)).ToArray();
        var affinity = structuredAuthors.Length > 0
            ? structuredAuthors.Select(person => AuthorAffinity.Evaluate(requested.Author, person.Name)).OrderByDescending(value => value.Score).First()
            : release.AuthorAffinity ?? AuthorAffinity.Evaluate(requested.Author, null);
        if (affinity.Kind != AuthorAffinityKind.Conflict && structuredName.AuthorAffinity is { } titleAffinity && titleAffinity.Score > affinity.Score)
            affinity = titleAffinity;
        if (structuredName.AuthorAffinity?.Kind == AuthorAffinityKind.Conflict) affinity = structuredName.AuthorAffinity;
        if (affinity.Kind != AuthorAffinityKind.Conflict && release.AuthorAffinity is { } releaseAffinity && releaseAffinity.Score > affinity.Score)
            affinity = releaseAffinity;
        // Never discard a conflicting explicit release credit just because structured metadata agrees.
        if (release.AuthorAffinity?.Kind == AuthorAffinityKind.Conflict) affinity = release.AuthorAffinity;
        var seriesEvidence = (release.SeriesEvidence ?? []).ToList();
        foreach (var candidateSeries in source.Work.Series)
        {
            var expected = requested.Series?.FirstOrDefault(series =>
                ReleaseTitleMatcher.Tokens(DeterministicBookMatcher.RemoveArticleVariants(series.Name)).SequenceEqual(
                    ReleaseTitleMatcher.Tokens(DeterministicBookMatcher.RemoveArticleVariants(candidateSeries.Name))));
            var comparable = !string.IsNullOrWhiteSpace(expected?.Position) && !string.IsNullOrWhiteSpace(candidateSeries.Position);
            var state = !comparable ? IdentityEvidenceState.Unknown :
                ExternalReleaseNameEvidence.PositionsEqual(expected!.Position!, candidateSeries.Position!) ? IdentityEvidenceState.Exact : IdentityEvidenceState.Conflicting;
            seriesEvidence.Add(new(state, candidateSeries.Name, expected?.Position, candidateSeries.Position,
                comparable ? "Structured series positions compared within the same series." : "Series or position unknown; no contradiction."));
        }
        var language = source.Edition?.Language ?? release.AssertedLanguage;
        var languageState = string.IsNullOrWhiteSpace(language) ? IdentityEvidenceState.Unknown :
            LanguageAcceptance.IsAcceptedOrUnspecified(language, requested.Language) ? IdentityEvidenceState.Compatible : IdentityEvidenceState.Conflicting;
        var contradictions = new List<string>();
        if (title.State == IdentityEvidenceState.Conflicting) contradictions.Add(title.Reason);
        if (affinity.Kind == AuthorAffinityKind.Conflict) contradictions.Add($"Author conflicts: requested {requested.Author}, observed {affinity.DetectedAuthor}.");
        contradictions.AddRange(seriesEvidence.Where(value => value.State == IdentityEvidenceState.Conflicting)
            .Select(value => $"Series {value.Name} position conflicts: requested {value.ExpectedPosition}, observed {value.ObservedPosition}."));
        if (languageState == IdentityEvidenceState.Conflicting) contradictions.Add($"Language conflicts: requested {requested.Language ?? "default accepted language"}, observed {language}.");
        var conditions = (release.Conditions ?? []).Concat(structuredName.Conditions ?? []).Distinct().ToList();
        if (source.Release?.IsCollection == true) conditions.Add(new(ReleaseConditionKind.EditionReview, "Structured release is a collection."));
        if (source.Release?.IsSample == true) conditions.Add(new(ReleaseConditionKind.EditionReview, "Structured release is a sample."));
        if (source.Release?.IsAbridged == true && source.Release.IsUnabridged != true) conditions.Add(new(ReleaseConditionKind.EditionReview, "Structured release is abridged."));
        var assessment = new CandidateIdentityAssessment(requested, new CandidateSourceMetadata(
            source.ProviderReference, source.Work, source.Edition, source.Release, source.ExtensionsJson), release, title, affinity, seriesEvidence,
            languageState, contradictions, conditions, WorkIdentityDecision.Ambiguous,
            [title.Reason, $"Author evidence: {affinity.Kind}.", "Unclassified descriptors are preserved without a penalty."]);
        var decision = contradictions.Count > 0 ? WorkIdentityDecision.Mismatch : !assessment.HasStrongTitle
            ? WorkIdentityDecision.Ambiguous : conditions.Count > 0 ? WorkIdentityDecision.MatchWithConditions : WorkIdentityDecision.Match;
        return assessment with { Decision = decision };
    }
}
