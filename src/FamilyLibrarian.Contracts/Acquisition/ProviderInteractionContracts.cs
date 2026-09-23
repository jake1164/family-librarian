namespace FamilyLibrarian.Contracts.Acquisition;

/// <summary>
/// Administrator-only summary of a provider acquisition that is waiting for a
/// human interaction. This intentionally excludes provider control URLs,
/// credentials, cookies, and remote-view connection details.
/// </summary>
public sealed record ProviderInteractionResponse(
    Guid ProviderAcquisitionJobId,
    Guid RequestId,
    Guid RequestFormatId,
    string ProviderId,
    string Type,
    string? Message,
    DateTimeOffset? ExpiresAtUtc,
    bool ResumeSupported,
    bool IsExpired,
    bool CanStart,
    bool CanUseFallback,
    bool CanCancel);
