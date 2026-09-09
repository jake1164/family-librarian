namespace FamilyLibrarian.Contracts.Delivery;

public sealed record DeliveryTargetResponse(
    Guid Id,
    string Provider,
    string Name,
    string Address,
    bool IsEnabled,
    bool SendByDefault,
    uint Version);

public sealed record SetKindleAddressRequest(string Address, uint? ExpectedVersion, bool SendByDefault);

public sealed record SetKindleEnabledRequest(bool Enabled, uint ExpectedVersion);

/// <summary>The admin accounts page's "set/change Kindle email" action. Deliberately no <c>SendByDefault</c> -- that request-time preference stays the account owner's own decision.</summary>
public sealed record AdminSetKindleAddressRequest(string Address, uint? ExpectedVersion);

/// <summary>The admin accounts page's Kindle column: whether it's configured, and whether it's enabled.</summary>
public sealed record KindleDeliverySummaryResponse(string Address, bool IsEnabled, uint Version);

public sealed record TestKindleDeliveryResponse(bool Succeeded, string Message);

public sealed record SendExistingBookRequest(Guid WorkId);

public sealed record SendExistingBookResponse(bool Succeeded, string? Message);
