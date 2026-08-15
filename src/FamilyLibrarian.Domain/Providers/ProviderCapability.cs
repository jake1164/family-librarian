namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// One thing an installed provider can do.
/// </summary>
/// <remarks>
/// A single provider may advertise several capabilities. Core code consumes a
/// capability rather than assuming every installed integration is an
/// acquisition source: metadata, ownership, availability, store offers, direct
/// legal acquisition, library storage, and delivery are independent concerns.
/// </remarks>
public enum ProviderCapability
{
    Metadata,
    Availability,
    StoreOffer,
    DirectAcquisition,
    OwnedLibrary,
    LibraryBackend,
    Delivery
}
