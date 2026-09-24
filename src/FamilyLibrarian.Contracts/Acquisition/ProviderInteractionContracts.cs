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
    bool CanCancel,
    // True once a verification session has been started and is still
    // unexpired -- the admin queue should offer "open remote view" rather
    // than "start verification" for this job.
    bool CanViewNow,
    // Best-effort context so an administrator scanning the interaction queue
    // can tell which book and requester a card is for without following a
    // link first. Null when the originating request could not be loaded
    // (e.g. deleted between the job's creation and this read) -- the card
    // still renders, just without this context.
    string? WorkTitle,
    IReadOnlyList<string>? Authors,
    string? RequesterDisplayName);
