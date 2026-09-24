namespace FamilyLibrarian.Domain.Providers;

/// <summary>
/// The administrator-selected order in which an external provider may use
/// its own subscription/quota and free-interactive acquisition paths.
/// </summary>
public enum ExternalProviderAcquisitionMode
{
    SubscriptionFirst,
    FreeFirst,
    SubscriptionOnly,
    FreeOnly
}
