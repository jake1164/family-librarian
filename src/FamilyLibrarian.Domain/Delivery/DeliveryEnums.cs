namespace FamilyLibrarian.Domain.Delivery;

/// <summary>
/// The mechanism used to get an owned book to a user's configured
/// <see cref="DeliveryTarget"/>. Only one exists today; see
/// docs/01-product-architecture-spec.md §15/§15.1 for the intended future
/// set (e.g. a direct-device or browser-download method).
/// </summary>
public enum DeliveryTargetProvider
{
    CwaKindleEmail = 1
}
