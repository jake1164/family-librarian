namespace FamilyLibrarian.Domain.Acquisition;

public enum AcquisitionJobStatus
{
    Created = 1,
    InProgress = 2,
    CandidateAcquired = 3,
    WaitingForSecurityScanner = 4,
    WaitingForPrivateEgress = 5,
    Failed = 6,
    Cancelled = 7
}

public enum EgressPolicy
{
    Normal = 1,
    PrivateRequired = 2,
    CustomProxy = 3
}

public enum AcquisitionCandidateStatus
{
    Discovered = 1,
    Selected = 2,
    Acquired = 3,
    Rejected = 4,
    Failed = 5
}

/// <summary>
/// Where a <see cref="MediaAsset"/> sits in the security pipeline's storage
/// zones (see docs/01-product-architecture-spec.md §13).
/// </summary>
public enum MediaAssetStorageState
{
    Quarantine = 1,
    Processing = 2,
    Rejected = 3,
    Trusted = 4,
    Archived = 5,
    /// <summary>
    /// The file passed security checks but its content could not be matched to
    /// the Work/request it was staged to fulfill. It is retained for later
    /// librarian handling. It can only return to processing for a fresh,
    /// deterministic identity verification before the usual approval checks.
    /// </summary>
    Unmatched = 6,
    /// <summary>
    /// The staged bytes were permanently removed. The asset row remains only
    /// as auditable evidence of a blocked or administrator-discarded file.
    /// </summary>
    Destroyed = 7
}
