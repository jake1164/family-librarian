using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Chooses among multiple acceptable audiobook recordings of the same
/// requested work, deterministically and without any popularity/download-count
/// acceptance gate. This is the "best recording among records" half of
/// audiobook selection -- see <c>GutenbergProvider.BuildBestAudiobookOption</c>
/// (and any other provider's own format-bundling) for the separate "best
/// file/codec within one record" half. The two are never collapsed into one
/// opaque method: this selector receives one already-format-resolved
/// candidate per record/edition, never raw per-codec bundles.
/// </summary>
/// <remarks>
/// Ranks by an ordered comparator chain rather than an additive score, so an
/// earlier dimension always dominates every later one -- a giant weighted
/// score lets several weak signals outvote one important requirement (an
/// abridged, high-bitrate recording beating a complete, low-bitrate one). Only
/// dimensions the application has reliable evidence for today are modeled:
/// completeness (<see cref="FulfillmentOption.IsAbridged"/>/<see cref="FulfillmentOption.IsUnabridged"/>),
/// narration (<see cref="FulfillmentOption.NarrationKind"/>), container/codec
/// packaging (<see cref="AudiobookFormatPolicy"/>), and finally popularity and
/// a stable ID as late, weak tiebreakers. Identity, language, and release
/// acceptability are enforced by the caller before candidates ever reach this
/// selector -- it never re-derives them.
/// <para>
/// Multiple acceptable candidates never force a review by themselves (that
/// would manufacture uncertainty merely because more than one record exists).
/// The comparator chain always ends in a unique stable-ID tiebreak, so an
/// acceptable candidate set always has exactly one winner. A review is
/// produced only for the one case that is genuine, unresolved uncertainty:
/// <see cref="AudiobookNarrationPreference.HumanOnly"/> with no candidate
/// whose narration is confirmed Human, but at least one whose narration is
/// merely Unknown rather than confirmed Synthetic.
/// </para>
/// </remarks>
public static class AudiobookCandidateSelector
{
    public static AudiobookCandidateSelectionResult Select(
        IReadOnlyList<FulfillmentOption> candidates,
        AudiobookNarrationPreference preference)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var evaluations = candidates.Select(candidate => Evaluate(candidate, preference)).ToArray();
        var accepted = evaluations.Where(evaluation => evaluation.Accepted).Select(evaluation => evaluation.Candidate).ToArray();

        if (accepted.Length == 0)
        {
            var uncertain = evaluations.Where(evaluation => evaluation.RequiresNarrationConfirmation).ToArray();
            var reason = uncertain.Length > 0
                ? "A copy was found, but its narration could not be confirmed as human, and Human narration only is required."
                : "No candidate met the audiobook acquisition requirements.";
            return new AudiobookCandidateSelectionResult(null, reason, evaluations);
        }

        var ranked = accepted
            .OrderBy(candidate => CompletenessRank(candidate))
            .ThenBy(candidate => NarrationRank(candidate.NarrationKind, preference))
            .ThenBy(candidate => AudiobookFormatPolicy.GetFormatRank(candidate.Format) ?? int.MaxValue)
            .ThenByDescending(candidate => candidate.ProviderPopularity ?? 0)
            .ThenBy(candidate => candidate.ProviderResultId, StringComparer.Ordinal)
            .ToArray();

        var winner = ranked[0];
        return new AudiobookCandidateSelectionResult(
            winner, DescribeSelection(winner, ranked, preference), evaluations);
    }

    private static AudiobookCandidateEvaluation Evaluate(
        FulfillmentOption candidate, AudiobookNarrationPreference preference)
    {
        if (preference == AudiobookNarrationPreference.HumanOnly)
        {
            if (candidate.NarrationKind == NarrationKind.Synthetic)
            {
                return new AudiobookCandidateEvaluation(
                    candidate, Accepted: false, RequiresNarrationConfirmation: false,
                    "Uses computer-generated narration; Human narration only is required.");
            }

            if (candidate.NarrationKind is null or NarrationKind.Unknown)
            {
                // Conservative on purpose: Unknown never satisfies HumanOnly,
                // but it is also not a confirmed rejection like Synthetic --
                // it is unresolved uncertainty, routed for review by the caller.
                return new AudiobookCandidateEvaluation(
                    candidate, Accepted: false, RequiresNarrationConfirmation: true,
                    "Its narration could not be confirmed as human, and Human narration only is required.");
            }
        }

        return new AudiobookCandidateEvaluation(candidate, Accepted: true, RequiresNarrationConfirmation: false, null);
    }

    /// <summary>0 (best) for a candidate not known to be abridged, 1 for one known to be.</summary>
    private static int CompletenessRank(FulfillmentOption candidate) => candidate.IsAbridged == true ? 1 : 0;

    /// <summary>
    /// 0 (best) only for a confirmed Human candidate under <see cref="AudiobookNarrationPreference.PreferHuman"/>.
    /// Every other combination ranks equally at this dimension -- <c>NoPreference</c>
    /// never distinguishes by narration, and neither Unknown nor Synthetic is
    /// treated as worse than the other under PreferHuman: Unknown is not evidence
    /// of Synthetic, so it must not be penalized as though it were (principle:
    /// unknown remains unknown).
    /// </summary>
    private static int NarrationRank(NarrationKind? kind, AudiobookNarrationPreference preference) =>
        preference == AudiobookNarrationPreference.PreferHuman && kind == NarrationKind.Human ? 0 : 1;

    private static string DescribeSelection(
        FulfillmentOption winner, FulfillmentOption[] ranked, AudiobookNarrationPreference preference)
    {
        var facts = new List<string> { $"Selected automatically: record #{winner.ProviderResultId}." };

        if (winner.NarrationKind == NarrationKind.Human)
        {
            facts.Add(string.IsNullOrWhiteSpace(winner.Narrator)
                ? "Human narration."
                : $"Human narration by {winner.Narrator}.");
        }
        else if (winner.NarrationKind == NarrationKind.Synthetic)
        {
            facts.Add("Computer-generated narration.");
        }

        if (winner.IsUnabridged == true)
        {
            facts.Add("Unabridged.");
        }

        if (ranked.Length > 1)
        {
            var runnerUp = ranked[1];
            if (preference == AudiobookNarrationPreference.PreferHuman &&
                winner.NarrationKind == NarrationKind.Human &&
                runnerUp.NarrationKind != NarrationKind.Human)
            {
                facts.Add($"Preferred over record #{runnerUp.ProviderResultId}, which does not have confirmed human narration.");
            }
            else if (CompletenessRank(winner) < CompletenessRank(runnerUp))
            {
                facts.Add($"Preferred over record #{runnerUp.ProviderResultId}, which is abridged.");
            }
        }

        return string.Join(' ', facts);
    }
}

public sealed record AudiobookCandidateEvaluation(
    FulfillmentOption Candidate,
    bool Accepted,
    bool RequiresNarrationConfirmation,
    string? Reason);

public sealed record AudiobookCandidateSelectionResult(
    FulfillmentOption? Winner,
    string DecisionReason,
    IReadOnlyList<AudiobookCandidateEvaluation> CandidateEvaluations)
{
    public bool CanAutoAcquire => Winner is not null;

    /// <summary>
    /// Candidates whose narration could not be confirmed against a
    /// <see cref="AudiobookNarrationPreference.HumanOnly"/> requirement --
    /// genuine unresolved uncertainty a librarian must resolve, distinct from
    /// a confirmed-Synthetic candidate, which is simply excluded.
    /// </summary>
    public IReadOnlyList<FulfillmentOption> CandidatesRequiringNarrationConfirmation => CandidateEvaluations
        .Where(evaluation => evaluation.RequiresNarrationConfirmation)
        .Select(evaluation => evaluation.Candidate with { RequiresNarrationConfirmation = true })
        .ToArray();
}
