namespace FamilyLibrarian.Contracts.Acquisition;

/// <summary>
/// No field here ever carries a server filesystem path — only opaque ids the
/// admin queue can use to look the asset back up.
/// </summary>
public sealed record ManualImportResultResponse(
    Guid AcquisitionJobId,
    Guid MediaAssetId);

/// <summary>
/// A protocol-v2 external-provider acquisition was durably submitted; no
/// file exists yet. Poll the request/format's usual progress view — the
/// background poller stages a file once the provider reports completion.
/// </summary>
public sealed record ManualAcquisitionInProgressResponse(Guid ProviderAcquisitionJobId);
