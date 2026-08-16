using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Policy;

namespace FamilyLibrarian.Application.Policy;

/// <summary>At most one recommended <see cref="FulfillmentOption"/>, with the reason it was picked.</summary>
public sealed record FulfillmentRecommendation(FulfillmentOption Option, string ProfileId, string Reason);

/// <summary>
/// Ranks an already-permitted set of <see cref="FulfillmentOption"/>s under a
/// named policy profile. Pure, in-process logic — no provider capability, no
/// network call. See <c>docs/02-domain-workflows.md</c>'s <c>ProviderPolicyProfile</c>
/// sketch: a policy ranks options that are already permitted, it does not
/// decide whether a provider may be used at all.
/// </summary>
public interface IPolicyRanker
{
    FulfillmentRecommendation? Recommend(IReadOnlyList<FulfillmentOption> options, string profileId);
}

/// <summary>
/// Four fixed, hardcoded profiles — not a generic rules language. Matches the
/// domain spec's own guidance that scoring/rule-language extensions come only
/// after real options exist, and mirrors <c>ProviderRegistry</c>'s hardcoded
/// allowlist precedent: no path by which configuration introduces new ranking
/// behavior.
/// </summary>
public sealed class PolicyRanker : IPolicyRanker
{
    public FulfillmentRecommendation? Recommend(IReadOnlyList<FulfillmentOption> options, string profileId)
    {
        if (options.Count == 0)
        {
            return null;
        }

        var winner = profileId switch
        {
            PolicyProfileIds.LibraryFirst => options.MinBy(KindRank),
            PolicyProfileIds.FreeFirst => options.MinBy(option => (CostOf(option), KindRank(option))),
            PolicyProfileIds.LowestCost => options.MinBy(CostOf),
            // ManualChoice, and any unrecognized id: fail closed to "no
            // recommendation" rather than guessing — same posture as an
            // unusable provider.
            _ => null
        };

        return winner is null ? null : new FulfillmentRecommendation(winner, profileId, DescribeReason(winner));
    }

    private static int KindRank(FulfillmentOption option) => option.OptionKind switch
    {
        OptionKind.Owned => 0,
        OptionKind.Availability => 1,
        OptionKind.DirectAcquisition => 2,
        OptionKind.StoreOffer => 3,
        OptionKind.ExternalAction => 4,
        _ => 5
    };

    private static decimal CostOf(FulfillmentOption option) => option.Cost ?? 0m;

    private static string DescribeReason(FulfillmentOption option) => option.OptionKind switch
    {
        OptionKind.Owned => "Already in your library",
        OptionKind.Availability when option.AcquisitionMethod == AcquisitionMethod.Borrow =>
            "Available to borrow from the library",
        OptionKind.DirectAcquisition when CostOf(option) == 0m => "Free to download",
        OptionKind.StoreOffer when CostOf(option) > 0m =>
            $"Purchase for {CostOf(option):C}{(string.IsNullOrWhiteSpace(option.Currency) ? string.Empty : $" {option.Currency}")}",
        _ => "Available"
    };
}
