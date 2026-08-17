namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// The allowed <see cref="MediaAsset"/> storage-state transitions.
/// </summary>
/// <remarks>
/// M9 only ever creates an asset in <see cref="MediaAssetStorageState.Quarantine"/>
/// and has no caller that moves it further: that is the concrete mechanism
/// behind "cannot be made available or delivered before M10's security
/// decision." The transition method and matrix exist so M10 can enforce them
/// without another migration, not so M9 can use them.
/// </remarks>
public static class MediaAssetStorageTransitions
{
    public const MediaAssetStorageState InitialState = MediaAssetStorageState.Quarantine;

    private static readonly Dictionary<MediaAssetStorageState, MediaAssetStorageState[]> Allowed = new()
    {
        [MediaAssetStorageState.Quarantine] =
        [
            MediaAssetStorageState.Processing,
            MediaAssetStorageState.Rejected,
            MediaAssetStorageState.Destroyed
        ],
        [MediaAssetStorageState.Processing] =
        [
            MediaAssetStorageState.Trusted,
            MediaAssetStorageState.Rejected,
            MediaAssetStorageState.Unmatched,
            MediaAssetStorageState.Destroyed,
            // A transient scan/validator error re-queues rather than condemning
            // the asset outright.
            MediaAssetStorageState.Quarantine
        ],
        [MediaAssetStorageState.Trusted] = [MediaAssetStorageState.Archived],
        [MediaAssetStorageState.Unmatched] =
        [
            MediaAssetStorageState.Rejected,
            MediaAssetStorageState.Destroyed
        ],
        [MediaAssetStorageState.Rejected] = [MediaAssetStorageState.Destroyed]
        // Archived and Destroyed are terminal. An unmatched asset can be
        // retained for later routing or explicitly discarded, but cannot be
        // approved or published.
    };

    public static bool IsAllowed(MediaAssetStorageState from, MediaAssetStorageState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;
}

public sealed class InvalidMediaAssetStorageTransitionException(
    MediaAssetStorageState from,
    MediaAssetStorageState to)
    : InvalidOperationException($"A media asset cannot move from {from} to {to}.")
{
    public MediaAssetStorageState From { get; } = from;

    public MediaAssetStorageState To { get; } = to;
}
