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

public sealed record TestKindleDeliveryResponse(bool Succeeded, string Message);
