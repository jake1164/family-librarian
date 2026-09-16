namespace FamilyLibrarian.Domain.Communications;

/// <summary>
/// One household member's Matrix identity link (COMM-1 §C) -- explicit and
/// revocable, per the Communications plan's §14 requirement. A link starts
/// unverified the moment a verification DM is sent and only becomes active
/// once the member replies with the code in that same room, proving they
/// control the Matrix ID they entered.
/// </summary>
public sealed class UserMatrixDestination
{
    private UserMatrixDestination()
    {
    }

    public UserMatrixDestination(Guid userId, DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid UserId { get; private set; }

    public string? MatrixUserId { get; private set; }

    /// <summary>The bot's direct-message room with <see cref="MatrixUserId"/>. Also the inbound router's lookup key.</summary>
    public string? RoomId { get; private set; }

    public string? VerificationCode { get; private set; }

    public DateTimeOffset? VerificationRequestedAtUtc { get; private set; }

    public DateTimeOffset? VerifiedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint Version { get; private set; }

    public bool IsVerified => VerifiedAtUtc is not null;

    /// <summary>
    /// Starts (or restarts) linking to a Matrix ID: a fresh code invalidates
    /// any previous one, and re-requesting always resets to unverified --
    /// entering a different Matrix ID mid-link is never silently merged with
    /// an old verification.
    /// </summary>
    public void RequestVerification(string matrixUserId, string roomId, string code, DateTimeOffset requestedAtUtc)
    {
        MatrixUserId = matrixUserId;
        RoomId = roomId;
        VerificationCode = code;
        VerificationRequestedAtUtc = requestedAtUtc;
        VerifiedAtUtc = null;
        UpdatedAtUtc = requestedAtUtc;
    }

    public void Verify(DateTimeOffset verifiedAtUtc)
    {
        VerifiedAtUtc = verifiedAtUtc;
        UpdatedAtUtc = verifiedAtUtc;
    }

    /// <summary>Explicit, user-initiated removal -- the link stops being a valid outbound/inbound destination.</summary>
    public void Unlink(DateTimeOffset unlinkedAtUtc)
    {
        MatrixUserId = null;
        RoomId = null;
        VerificationCode = null;
        VerificationRequestedAtUtc = null;
        VerifiedAtUtc = null;
        UpdatedAtUtc = unlinkedAtUtc;
    }
}
