using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>One source's current claim of a publication date.</summary>
public sealed record DateClaim(string ProviderId, string SourceEntityId, DateOnly Date, DatePrecision Precision, DateBasis Basis)
{
    public (DateOnly First, DateOnly Last) Interval => PublicationStatusPolicy.Interval(Date, Precision);
}

/// <param name="Selected">The claim whose date the catalog shows.</param>
/// <param name="Conflict">The best-ranked claims name disjoint intervals: the shown value is the earliest, and the disagreement is reportable.</param>
public sealed record DateSelection(DateClaim Selected, bool Conflict);

/// <summary>
/// Chooses the displayed publication date from the current claims, as a pure function of the claim set. A claim of the original
/// date outranks one of unknown meaning, which outranks an edition-derived fallback; within the best rank the most precise value
/// consistent with the others is shown. The fallback is kept as history, never as the answer once original evidence exists.
/// </summary>
public static class PublicationDateSelection
{
    public static DateSelection? Select(IReadOnlyCollection<DateClaim> currentClaims)
    {
        ArgumentNullException.ThrowIfNull(currentClaims);
        if (currentClaims.Count == 0)
        {
            return null;
        }

        var bestRank = currentClaims.Min(Rank);
        var tier = currentClaims.Where(claim => Rank(claim) == bestRank).ToArray();

        // Calendar intervals are nested or disjoint, never partly overlapping, so "inside every other claim's interval" exists
        // exactly when the tier's intervals form one chain.
        var inside = tier
            .Where(claim => tier.All(other => other.Interval.First <= claim.Interval.First && claim.Interval.Last <= other.Interval.Last))
            .OrderBy(claim => claim.ProviderId, StringComparer.Ordinal)
            .ThenBy(claim => claim.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(claim => claim.Date)
            .ToArray();
        if (inside.Length > 0)
        {
            return new DateSelection(inside[0], false);
        }

        var earliest = tier
            .OrderBy(claim => claim.Interval.First)
            .ThenBy(claim => claim.ProviderId, StringComparer.Ordinal)
            .ThenBy(claim => claim.SourceEntityId, StringComparer.Ordinal)
            .ThenBy(claim => claim.Precision)
            .First();
        return new DateSelection(earliest, true);
    }

    private static int Rank(DateClaim claim) => claim.Basis switch
    {
        DateBasis.Original => 0,
        DateBasis.Unknown => 1,
        _ => 2
    };
}
