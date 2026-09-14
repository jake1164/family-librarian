namespace FamilyLibrarian.Contracts.Communications;

/// <summary>A household member's own Matrix link state (COMM-1 §C) -- distinct from admin provider config.</summary>
public sealed record MatrixLinkStatusResponse(bool IsVerified, string? MatrixUserId, bool AwaitingVerification);

public sealed record RequestMatrixLinkRequest(string MatrixUserId);
