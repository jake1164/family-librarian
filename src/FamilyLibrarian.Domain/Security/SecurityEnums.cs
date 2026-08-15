namespace FamilyLibrarian.Domain.Security;

public enum SecurityEvaluationStatus
{
    Pending = 1,
    Passed = 2,
    Failed = 3,
    ReviewRequired = 4
}

public enum ScanResultStatus
{
    Clean = 1,
    Detected = 2,
    Error = 3,
    Unavailable = 4
}

public enum ApprovalDecision
{
    Approved = 1,
    Rejected = 2
}

public enum ApprovalActorType
{
    Admin = 1,
    Policy = 2,
    System = 3
}
