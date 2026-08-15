namespace FamilyLibrarian.Contracts.Acquisition;

/// <summary>
/// No field here ever carries a server filesystem path — only opaque ids the
/// admin queue can use to look the asset back up.
/// </summary>
public sealed record ManualImportResultResponse(
    Guid AcquisitionJobId,
    Guid MediaAssetId);
