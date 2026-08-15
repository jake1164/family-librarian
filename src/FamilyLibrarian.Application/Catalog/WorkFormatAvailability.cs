using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Catalog;

/// <summary>
/// Whether and how one Work's format is available to the requesting user right
/// now — a catalogue read model, not a request or a delivery log.
/// </summary>
/// <remarks>
/// M8 ships this as a purely computed value with no backing table: until M9
/// introduces <c>MediaAsset</c>, <see cref="OwnershipState"/> is always
/// <see cref="Catalog.OwnershipState.NotOwned"/>, and only
/// <see cref="AcquisitionState"/> (derived from the caller's own
/// <see cref="RequestFormat"/> rows) carries real information. This is expected,
/// not a placeholder to work around.
/// </remarks>
public sealed record WorkFormatAvailability(
    Guid WorkId,
    RequestMediaType MediaType,
    OwnershipState OwnershipState,
    Guid? PreferredAssetId,
    Guid? PreferredEditionId,
    AcquisitionState? AcquisitionState);

public enum OwnershipState
{
    NotOwned,
    Owned
}

/// <summary>Mirrors <see cref="RequestFormatStatus"/> for catalogue display.</summary>
public enum AcquisitionState
{
    Requested,
    NotAvailable,
    Cancelled
}
