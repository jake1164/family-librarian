namespace FamilyLibrarian.Domain.Publishing;

public enum CwaTransportMode
{
    Local = 1,
    Sftp = 2
}

/// <summary>
/// A lean status set for a synchronous-handoff, manual-recheck workflow — not
/// the fuller Pending/Staging/Importing/Verifying/Available/Failed/Cancelled
/// machine sketched in the architecture docs. There is exactly one CWA
/// destination in this deployment, so that generality is not needed yet.
/// </summary>
public enum LibraryImportStatus
{
    Publishing = 1,
    AwaitingVerification = 2,
    Available = 3,
    Failed = 4
}

public enum DeliveryStatus
{
    Uploading = 1,
    Verifying = 2,
    Delivered = 3,
    Failed = 4
}
