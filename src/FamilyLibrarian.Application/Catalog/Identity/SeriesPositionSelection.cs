using System.Globalization;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>One source entity's current statement of a series position.</summary>
/// <param name="ProviderId">Also the source's evidence family until providers declare one: no two providers are assumed independent.</param>
/// <param name="SourceEntityId">The provider's own id for the record that made the statement.</param>
/// <param name="Label">The position text exactly as the source gave it.</param>
/// <param name="Sort">The numeric position when the source gave one.</param>
public sealed record PositionClaim(string ProviderId, string SourceEntityId, string? Label, decimal? Sort)
{
    /// <summary>The comparison value: the numeric position when there is one, else the normalized label.</summary>
    public string ValueKey => Sort is { } sort
        ? sort.ToString("0.###", CultureInfo.InvariantCulture)
        : "label:" + (Label ?? string.Empty).Trim().ToUpperInvariant();
}

/// <param name="Selected">The value the catalog shows.</param>
/// <param name="State">SingleSource, Agreed (sources agree; independence is not established) or Conflicted (they disagree).</param>
/// <param name="DistinctValues">The distinct values stated, so a reader can see what dissented.</param>
public sealed record PositionSelection(PositionClaim Selected, SeriesPositionState State, IReadOnlyList<string> DistinctValues);

/// <summary>
/// Chooses the displayed position of a Work in a series from the current claims, as a pure function of the claim set: the same
/// claims give the same answer in whatever order they arrived. Disagreement is always visible; correlation between sources may
/// limit how much agreement is worth, never hide that sources disagree.
/// </summary>
public static class SeriesPositionSelection
{
    /// <param name="currentClaims">Each source entity's latest claim. Empty gives no selection.</param>
    public static PositionSelection? Select(IReadOnlyCollection<PositionClaim> currentClaims)
    {
        ArgumentNullException.ThrowIfNull(currentClaims);
        if (currentClaims.Count == 0)
        {
            return null;
        }

        var byValue = currentClaims.GroupBy(claim => claim.ValueKey, StringComparer.Ordinal).ToArray();
        var distinctSources = currentClaims.Select(claim => (claim.ProviderId, claim.SourceEntityId)).Distinct().Count();
        var state = byValue.Length > 1
            ? SeriesPositionState.Conflicted
            : distinctSources >= 2 ? SeriesPositionState.Agreed : SeriesPositionState.SingleSource;

        // The value most distinct evidence families support wins; ties fall to the lowest number, then provider, source and value
        // ordinal, so the choice never depends on arrival order.
        var winner = byValue
            .OrderByDescending(group => group.Select(claim => claim.ProviderId).Distinct(StringComparer.Ordinal).Count())
            .ThenBy(group => group.First().Sort is null)
            .ThenBy(group => group.Min(claim => claim.Sort))
            .ThenBy(group => group.Min(claim => claim.ProviderId), StringComparer.Ordinal)
            .ThenBy(group => group.Min(claim => claim.SourceEntityId), StringComparer.Ordinal)
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .First();
        var selected = winner
            .OrderBy(claim => claim.ProviderId, StringComparer.Ordinal)
            .ThenBy(claim => claim.SourceEntityId, StringComparer.Ordinal)
            .First();
        return new PositionSelection(selected, state, byValue.Select(group => group.Key).Order(StringComparer.Ordinal).ToArray());
    }
}
