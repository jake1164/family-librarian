using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Application.SemanticAssessment;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

public sealed partial class ExternalProviderRecheckService
{
    private sealed record TriageSelection(FulfillmentOption[] Matches, AudiobookPartSetSelection? Parts,
        CandidateTriageAuthorization? Authorization = null, string? VetoedId = null);

    private async Task<TriageSelection> ApplyCandidateTriageAsync(BookRequest request, RequestFormat format,
        Domain.Providers.ExternalProvider provider, BookIdentity identity, IReadOnlyList<FulfillmentOption> options,
        FulfillmentOption[] automatic, AudiobookPartSetSelection? parts, CancellationToken cancellationToken)
    {
        if (candidateTriage is null) return new(automatic, parts);
        // The pooled search nominates one strict winner by clearing competing MatchBasis stamps.
        // Retain those candidates' independently verified evidence for a grounded tie-break or veto fallback.
        var independent = options.Select(IndependentEvidence).ToArray();
        var competitors = CandidateConfidence.Rank(independent.Where(o =>
            CandidateConfidence.Allows(provider.EffectiveConfidence(format.MediaType), CandidateConfidence.Classify(o))), format.MediaType).ToArray();
        FulfillmentOption? Pick(IReadOnlyList<FulfillmentOption> matches) => matches.Count == 0 ? null : matches[0];
        var pick = parts?.Parts[0] ?? Pick(automatic);
        if (parts is null && format.MediaType == RequestMediaType.Audiobook)
            pick = AudiobookCandidateSelector.Select(automatic, await GetNarrationPreferenceAsync(request.UserId, cancellationToken)).Winner;
        var tied = pick is null || parts is not null ? [] : competitors
            .Where(o => ExternalCandidateRanker.AreTied(pick, o, format.MediaType)).Select(o => o.ProviderResultId).ToArray();
        var ordered = (parts?.Parts ?? []).Concat(competitors)
            .Concat(independent.Where(CandidateTriageRules.IsAuthorOnlyConflict)).Concat(independent)
            .DistinctBy(o => o.ProviderResultId, StringComparer.Ordinal).ToArray();
        var context = new CandidateTriageContext(identity, ordered, pick, tied, provider.EffectiveConfidence(format.MediaType));
        var result = await candidateTriage.GetAsync(request.Id, format.Id, provider.ProviderId, context, cancellationToken);
        if (result is null) return new(automatic, parts);
        if (result.Move != "veto")
        {
            await candidateTriage.ObserveSelectionAsync(result, cancellationToken);
            return new([result.Candidate], null, result.Authorization);
        }

        var remaining = independent.Where(o => o.ProviderResultId != result.Candidate.ProviderResultId).ToArray();
        var matches = CandidateConfidence.Rank(remaining.Where(o =>
            CandidateConfidence.Allows(context.Level, CandidateConfidence.Classify(o))), format.MediaType).ToArray();
        if (matches.Length == 0 && SameWorkCandidateSelector.TrySelect(ReviewableForRequester(remaining, identity.Language),
            identity, format.MediaType, context.Level) is { } same && CandidateConfidence.Allows(context.Level, same.Tier))
            matches = [same.Winner];
        var next = Pick(matches);
        if (format.MediaType == RequestMediaType.Audiobook)
            next = AudiobookCandidateSelector.Select(matches, await GetNarrationPreferenceAsync(request.UserId, cancellationToken)).Winner;
        // Vetoing a member defers that companion set; a standalone fallback still gets all normal checks.
        await candidateTriage.ObserveSelectionAsync(result, cancellationToken);
        return new(matches, null, next is null ? null : new CandidateTriageAuthorization(request.Id, format.Id, next,
            "veto", [result.Candidate.ProviderResultId]), result.Candidate.ProviderResultId);
    }

    private static FulfillmentOption IndependentEvidence(FulfillmentOption option) => option.MatchBasis is null &&
        option.IdentityAssessment is { HasStrongTitle: true, Decision: WorkIdentityDecision.Match or WorkIdentityDecision.MatchWithConditions } assessment
        ? option with { MatchBasis = assessment.AuthorEvidence.HasStrongSupport ? BookMatchBasis.StrictTitleAuthor : BookMatchBasis.StrictTitle }
        : option;
}
