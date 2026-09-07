namespace FamilyLibrarian.Contracts.Delivery;

public sealed record DeliveryTargetResponse(
    Guid Id,
    string Provider,
    string Name,
    string Address,
    bool IsEnabled,
    uint Version);

public sealed record SetKindleAddressRequest(string Address, uint? ExpectedVersion);

public sealed record SetKindleEnabledRequest(bool Enabled, uint ExpectedVersion);
