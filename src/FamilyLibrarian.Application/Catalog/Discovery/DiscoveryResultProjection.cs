namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// Projects one <see cref="DiscoveryResult{T}"/> into another item type without changing what it says about the read: the
/// outcome, completion, continuation, scope, observation time, usage and every note survive, and no second provider read or
/// second charge is possible because only the already-obtained items are mapped. Used so a series-only operation is a pure
/// view over the claims operation.
/// </summary>
public static class DiscoveryResultProjection
{
    /// <summary>Maps the items and may add notes of its own (for example a dropped duplicate); the source notes are always kept.</summary>
    public static DiscoveryResult<TTo> Map<TFrom, TTo>(
        DiscoveryResult<TFrom> source,
        Func<IReadOnlyList<TFrom>, (IReadOnlyList<TTo> Items, IReadOnlyList<string> Notes)> map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        var (items, added) = source.Items.Count == 0 ? ([], []) : map(source.Items);
        string[] notes = [.. source.Notes, .. added.Where(note => !source.Notes.Contains(note))];

        switch (source.Outcome)
        {
            case DiscoveryOutcome.Ok:
                return items.Count == 0
                    ? DiscoveryResult.Empty<TTo>(source.Scope, source.ObservedAtUtc, source.Usage, notes)
                    : DiscoveryResult.Found(items, source.Scope, source.Completion, source.ObservedAtUtc, source.Usage, source.Next, notes);
            case DiscoveryOutcome.Empty:
                return DiscoveryResult.Empty<TTo>(source.Scope, source.ObservedAtUtc, source.Usage, notes);
            case DiscoveryOutcome.Ambiguous:
                return DiscoveryResult.Choices(items, source.Scope, source.ObservedAtUtc, source.Usage, notes);
            case DiscoveryOutcome.Partial:
                return DiscoveryResult.PartialSuccess(items, source.Scope, source.ObservedAtUtc, source.Usage, notes);
            case DiscoveryOutcome.NoUsableItems:
                return DiscoveryResult.NoUsableItems<TTo>(
                    source.Scope, source.Completion, source.Next, source.ObservedAtUtc, source.Usage, notes);
            default:
                return items.Count > 0
                    ? DiscoveryResult.Interrupted(source.Outcome, items, source.Scope, source.ObservedAtUtc, source.Usage, notes)
                    : DiscoveryResult.NotAnswered<TTo>(source.Outcome, source.Scope, source.ObservedAtUtc, source.Usage, notes);
        }
    }

    /// <summary>
    /// The distinct series of a book's claims, in first-seen order. A bound series is the same series when its native id is
    /// equal; an unbound one is the same only when its label is equal. A repeat is reported as <see cref="DiscoveryCodes.DuplicateDropped"/>.
    /// </summary>
    public static DiscoveryResult<SeriesRef> SeriesOnly(DiscoveryResult<BookSeriesClaim> claims) =>
        Map<BookSeriesClaim, SeriesRef>(claims, items =>
        {
            var distinct = new List<SeriesRef>();
            var dropped = false;
            foreach (var claim in items)
            {
                var series = claim.Series;
                var known = distinct.Any(other => series.Id is not null
                    ? other.Id == series.Id
                    : other.Id is null && string.Equals(other.Label, series.Label, StringComparison.Ordinal));
                if (known)
                {
                    dropped = true;
                }
                else
                {
                    distinct.Add(series);
                }
            }

            return (distinct, dropped ? [DiscoveryCodes.DuplicateDropped] : []);
        });
}
