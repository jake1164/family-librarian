using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Orders equally-acceptable external provider candidates for the same work,
/// best first, so that more than one plausible copy existing is never by
/// itself a reason to stop and ask a human.
/// </summary>
/// <remarks>
/// An ordered comparator chain rather than an additive score, for the reason
/// <c>AudiobookCandidateSelector</c> already documents: a weighted score lets
/// several weak signals outvote one important requirement. The chain ends in a
/// unique, stable tiebreak, so an accepted candidate set always has exactly one
/// winner <em>and</em> a reproducible fallback order — the latter being what
/// lets a failed download advance to the next candidate instead of stalling.
/// <para>
/// Identity, language and release acceptability are decided before candidates
/// reach this ranker and are never re-derived here; the first dimension only
/// orders by the confidence already established. Provider
/// <c>extensions</c> — seeder and grab counts in particular — are deliberately
/// not inputs: protocol v2 §11 requires unknown extension content to be inert
/// and forbids it influencing a matching or trust decision, and a popularity
/// number chosen by the source is exactly the kind of provider self-assessment
/// Family Librarian does not delegate ranking to.
/// </para>
/// </remarks>
public static class ExternalCandidateRanker
{
    /// <summary>
    /// Orders <paramref name="candidates"/> best-first. Ties are impossible:
    /// the final dimension is the distinct <see cref="Catalog.FulfillmentOption.ProviderResultId"/>.
    /// </summary>
    public static IReadOnlyList<Catalog.FulfillmentOption> Rank(
        IEnumerable<Catalog.FulfillmentOption> candidates, RequestMediaType mediaType)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .OrderBy(IdentityRank)
            .ThenByDescending(candidate => candidate.HasPlausibleTitle)
            .ThenByDescending(candidate => candidate.AuthorAffinity?.Score ?? 0)
            .ThenBy(ReleaseConcernRank)
            .ThenBy(candidate => FormatRank(candidate, mediaType))
            .ThenBy(NarrationRank)
            .ThenBy(QualityRank)
            .ThenBy(candidate => candidate.ProviderResultId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The single best candidate, or <see langword="null"/> for an empty set.</summary>
    public static Catalog.FulfillmentOption? SelectBest(
        IEnumerable<Catalog.FulfillmentOption> candidates, RequestMediaType mediaType)
    {
        var ranked = Rank(candidates, mediaType);
        return ranked.Count == 0 ? null : ranked[0];
    }

    /// <summary>
    /// Corroborated identifier evidence first, then strict title-and-author
    /// equivalence, then the broad reviewable fallback.
    /// </summary>
    private static int IdentityRank(Catalog.FulfillmentOption candidate) => candidate.MatchBasis switch
    {
        Matching.BookMatchBasis.Identifier => 0,
        Matching.BookMatchBasis.StrictTitleAuthor or Matching.BookMatchBasis.StrictTitle => 1,
        Matching.BookMatchBasis.TitleAuthor => 2,
        _ => 3
    };

    /// <summary>
    /// A clean release first; then the specific "DRM state unconfirmed" case,
    /// which the acquisition path already tolerates under download-time
    /// validation; then any other release concern.
    /// </summary>
    private static int ReleaseConcernRank(Catalog.FulfillmentOption candidate)
    {
        if (!candidate.RequiresReleaseConfirmation)
        {
            return 0;
        }

        return string.Equals(
            candidate.ReleaseConcern, ExternalReleasePolicy.UnknownDrmConfirmationReason, StringComparison.Ordinal)
            ? 1
            : 2;
    }

    /// <summary>
    /// Each medium's own existing format policy. An unrecognized or absent
    /// container ranks last rather than being excluded: a source that reports
    /// only a release name often cannot state a container at all, and dropping
    /// those candidates reported "nothing found" for a request that in fact had
    /// usable results. The post-download structural validator remains the real
    /// gate on whether the bytes are a usable book.
    /// </summary>
    private static int FormatRank(Catalog.FulfillmentOption candidate, RequestMediaType mediaType) =>
        mediaType == RequestMediaType.Audiobook
            ? Acquisition.AudiobookFormatPolicy.GetFormatRank(candidate.Format) ?? int.MaxValue
            : string.IsNullOrWhiteSpace(candidate.Format)
                ? int.MaxValue
                : ExternalEbookFormatPolicy.AcquisitionPreference(candidate.Format);

    /// <summary>
    /// A recording with a confirmed human reader before one whose narration is
    /// simply unknown.
    /// </summary>
    /// <remarks>
    /// A late, weak tiebreak among candidates that are already equally
    /// acceptable — not a narration policy. The actual
    /// <c>AudiobookNarrationPreference</c> decision (including the
    /// <c>HumanOnly</c> requirement and its review path) stays in
    /// <c>AudiobookCandidateSelector</c>, which is the only place that sees the
    /// requester's preference.
    /// <para>
    /// This dimension exists because the single confirmed candidate is chosen
    /// before that selector ever runs, so without it a credited reader found in
    /// a release name would lose to an unknown-narration record on nothing but
    /// an ID comparison. Unknown is never treated as worse than Synthetic here
    /// — Synthetic candidates are excluded by the selector, not ranked down.
    /// </para>
    /// </remarks>
    private static int NarrationRank(Catalog.FulfillmentOption candidate) =>
        candidate.NarrationKind == Catalog.NarrationKind.Human ? 0 : 1;

    /// <summary>
    /// A release the source itself tagged as retail before an untagged one.
    /// This is a provenance fact the source stated, not a quality score
    /// invented here.
    /// </summary>
    private static int QualityRank(Catalog.FulfillmentOption candidate) =>
        string.Equals(candidate.Quality, "retail", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
}
