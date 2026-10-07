using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// The parts of one complete numbered audiobook, in part order.
/// </summary>
public sealed record AudiobookPartSetSelection(IReadOnlyList<FulfillmentOption> Parts)
{
    public string ProviderId => Parts[0].ProviderId;

    public WorkIdentityDecision WorkIdentity => Parts.Any(part => part.IdentityAssessment?.Decision == WorkIdentityDecision.Mismatch)
        ? WorkIdentityDecision.Mismatch : Parts.All(part => part.HasPlausibleTitle)
            ? WorkIdentityDecision.Match : WorkIdentityDecision.Ambiguous;

    public bool IsComplete => Parts.Count == Total && Parts.Select(part => part.AudiobookPart!.Number)
        .Order().SequenceEqual(Enumerable.Range(1, Total));

    public long? TotalSizeBytes => Parts.All(part => part.SizeBytes is not null)
        ? Parts.Sum(part => part.SizeBytes!.Value) : null;

    public CandidateAcquisitionAssessment AcquisitionAssessment =>
        new(IsComplete && WorkIdentity == WorkIdentityDecision.Match ? AcquisitionSuitability.EligibleForChecks
            : AcquisitionSuitability.NeedsCompanionParts,
            [IsComplete ? "Every numbered companion is present and compatible; normal file safety checks still apply."
                : "The numbered companion set is incomplete."]);

    public int Total => Parts[0].AudiobookPart!.Total!.Value;

    public IReadOnlyList<string> MemberResultIds { get; } = Parts.Select(part => part.ProviderResultId).ToArray();

    public bool HasSameMembers(IEnumerable<string> resultIds) =>
        resultIds.ToHashSet(StringComparer.Ordinal).SetEquals(MemberResultIds);
}

/// <summary>
/// Decides, deterministically, whether a provider's audiobook results contain
/// exactly one complete, compatible numbered set that may be acquired without
/// a librarian.
/// </summary>
/// <remarks>
/// Strict by design: anything ambiguous, incomplete or carrying any concern
/// beyond "this is one fragment" yields <c>null</c> and keeps the ordinary
/// review path. Completeness and edition compatibility come from
/// <see cref="ExternalAudiobookPartSetAssessment"/>; this adds the per-member
/// eligibility gates that an unattended download needs.
/// </remarks>
public static class AudiobookPartSetSelector
{
    /// <summary>Bounds the number of unattended downloads one request can start.</summary>
    public const int MaxParts = 8;

    public static AudiobookPartSetSelection? TrySelect(IEnumerable<FulfillmentOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var eligible = options.Where(IsEligibleMember).ToArray();
        var complete = new List<AudiobookPartSetSelection>();
        foreach (var group in eligible.GroupBy(option =>
                     (Provider: option.ProviderId.ToUpperInvariant(), Total: option.AudiobookPart!.Total!.Value)))
        {
            var anchor = group.Where(option => option.AudiobookPart!.Number == 1)
                .OrderBy(option => option.ProviderResultId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (anchor is null)
            {
                continue;
            }

            var assessment = ExternalAudiobookPartSetAssessment.For(anchor, eligible);
            // Every member needs independently decisive title evidence. Missing
            // author or packaging details cannot substitute for a contradiction.
            if (assessment.HasEveryNumber && assessment.Parts.Count == group.Key.Total &&
                assessment.Parts.Any(part => IsStrictBasis(part.MatchBasis)))
            {
                complete.Add(new AudiobookPartSetSelection(assessment.Parts));
            }
        }

        // Two different complete sets (for example two totals) is a genuine
        // choice between editions, not something to resolve unattended.
        return complete.Count == 1 ? complete[0] : null;
    }

    private static bool IsStrictBasis(BookMatchBasis? basis) =>
        basis is BookMatchBasis.Identifier or BookMatchBasis.StrictTitleAuthor or BookMatchBasis.StrictTitle;

    private static bool IsEligibleMember(FulfillmentOption option) =>
        option.MediaType == RequestMediaType.Audiobook &&
        option.AudiobookPart is { Total: { } total } part &&
        total is >= 2 and <= MaxParts &&
        part.Number >= 1 && part.Number <= total &&
        option.FragmentOnlyConcern &&
        !option.RequiresLanguageConfirmation &&
        option.HasPlausibleTitle &&
        IsStrictBasis(option.MatchBasis) &&
        option.AuthorAffinity?.Kind != AuthorAffinityKind.Conflict &&
        (string.IsNullOrWhiteSpace(option.Format) || AudiobookFormatPolicy.IsUsableForAutomaticAcquisition(option.Format)) &&
        !string.Equals(option.DrmStatus, "encrypted", StringComparison.OrdinalIgnoreCase) &&
        !(option.IsAbridged == true && option.IsUnabridged != true) &&
        option.SizeBytes is null or > 0;
}
