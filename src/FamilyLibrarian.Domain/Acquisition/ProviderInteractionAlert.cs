namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// One coalesced Matrix notify/authorize alert for a provider (HUMAN-ACQ-1 D5: at
/// most one open alert per <see cref="ExternalProviderId"/>, enforced at the
/// database by a partial unique index on <c>state IN ('Open','Claimed')</c>). Fans
/// out to every verified-Matrix administrator as a <see cref="ProviderInteractionAlertRecipient"/>;
/// the alert is not bound to a specific job — at claim time it resolves to that
/// provider's oldest claimable waiting job (D5).
/// </summary>
public sealed class ProviderInteractionAlert
{
    private readonly List<ProviderInteractionAlertRecipient> _recipients = [];

    private ProviderInteractionAlert()
    {
    }

    public ProviderInteractionAlert(
        Guid externalProviderId,
        string providerId,
        string providerDisplayName,
        DateTimeOffset createdAtUtc)
    {
        if (externalProviderId == Guid.Empty)
        {
            throw new ArgumentException("An external provider ID is required.", nameof(externalProviderId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDisplayName);

        Id = Guid.NewGuid();
        ExternalProviderId = externalProviderId;
        ProviderId = providerId.Trim();
        ProviderDisplayName = providerDisplayName.Trim();
        State = ProviderInteractionAlertState.Open;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid ExternalProviderId { get; private set; }

    public string ProviderId { get; private set; } = null!;

    /// <summary>Snapshotted at creation so a later rename of the provider does not rewrite already-sent messages.</summary>
    public string ProviderDisplayName { get; private set; } = null!;

    public ProviderInteractionAlertState State { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public Guid? ClaimedJobId { get; private set; }

    public Guid? ClaimedByUserId { get; private set; }

    /// <summary>Snapshotted at claim time so an edited Matrix message can name the claimer even if their display name later changes.</summary>
    public string? ClaimedByDisplayName { get; private set; }

    public DateTimeOffset? ClaimedAtUtc { get; private set; }

    public DateTimeOffset? ClosedAtUtc { get; private set; }

    /// <summary>e.g. "verified", "no-longer-needed", "lease-lapsed", "expired", "cancelled".</summary>
    public string? CloseReason { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyCollection<ProviderInteractionAlertRecipient> Recipients => _recipients;

    public bool IsOpen => State is ProviderInteractionAlertState.Open or ProviderInteractionAlertState.Claimed;

    public ProviderInteractionAlertRecipient AddRecipient(Guid userId)
    {
        var recipient = new ProviderInteractionAlertRecipient(Id, userId);
        _recipients.Add(recipient);
        return recipient;
    }

    public void Claim(Guid jobId, Guid userId, string claimedByDisplayName, DateTimeOffset atUtc)
    {
        RequireTransition(ProviderInteractionAlertState.Claimed);

        ClaimedJobId = jobId;
        ClaimedByUserId = userId;
        ClaimedByDisplayName = claimedByDisplayName.Trim();
        ClaimedAtUtc = atUtc;
        State = ProviderInteractionAlertState.Claimed;
    }

    public void TransferClaim(Guid userId, string claimedByDisplayName, DateTimeOffset atUtc)
    {
        if (State != ProviderInteractionAlertState.Claimed)
        {
            throw new InvalidProviderInteractionAlertTransitionException(State, ProviderInteractionAlertState.Claimed);
        }

        ClaimedByUserId = userId;
        ClaimedByDisplayName = claimedByDisplayName.Trim();
        ClaimedAtUtc = atUtc;
        foreach (var recipient in _recipients)
        {
            recipient.ResetRenderedState();
        }
    }

    public void Resolve(string closeReason, DateTimeOffset atUtc)
    {
        RequireTransition(ProviderInteractionAlertState.Resolved);
        Close(ProviderInteractionAlertState.Resolved, closeReason, atUtc);
    }

    public void Supersede(string closeReason, DateTimeOffset atUtc)
    {
        RequireTransition(ProviderInteractionAlertState.Superseded);
        Close(ProviderInteractionAlertState.Superseded, closeReason, atUtc);
    }

    public void Expire(DateTimeOffset atUtc)
    {
        RequireTransition(ProviderInteractionAlertState.Expired);
        Close(ProviderInteractionAlertState.Expired, "expired", atUtc);
    }

    private void Close(ProviderInteractionAlertState to, string closeReason, DateTimeOffset atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(closeReason);
        State = to;
        CloseReason = closeReason.Trim();
        ClosedAtUtc = atUtc;
    }

    private void RequireTransition(ProviderInteractionAlertState to)
    {
        if (!ProviderInteractionAlertTransitions.IsAllowed(State, to))
        {
            throw new InvalidProviderInteractionAlertTransitionException(State, to);
        }
    }
}

/// <summary>One administrator's copy of a <see cref="ProviderInteractionAlert"/> — a hashed, single-use magic link.</summary>
public sealed class ProviderInteractionAlertRecipient
{
    public const int MaxTokenHashLength = 128;
    public const int MaxRoomOrEventIdLength = 256;
    public const int MaxLastErrorLength = 1_024;

    private ProviderInteractionAlertRecipient()
    {
    }

    internal ProviderInteractionAlertRecipient(Guid alertId, Guid userId)
    {
        Id = Guid.NewGuid();
        AlertId = alertId;
        UserId = userId;
        DeliveryState = ProviderInteractionRecipientDeliveryState.Pending;
        SendAttempts = 0;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();

    public Guid AlertId { get; private set; }

    public ProviderInteractionAlert Alert { get; private set; } = null!;

    public Guid UserId { get; private set; }

    public ProviderInteractionRecipientDeliveryState DeliveryState { get; private set; }

    /// <summary>Set only when a send succeeds. The plaintext token is never stored (WP7).</summary>
    public string? TokenHash { get; private set; }

    public DateTimeOffset? TokenConsumedAtUtc { get; private set; }

    public DateTimeOffset? TokenRevokedAtUtc { get; private set; }

    public string? RoomId { get; private set; }

    public string? EventId { get; private set; }

    public int SendAttempts { get; private set; }

    /// <summary>Sanitized failure detail. Must never contain the token or the link.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? SentAtUtc { get; private set; }

    /// <summary>Which alert state the Matrix message currently shows, so the worker only edits when it has actually changed.</summary>
    public string? RenderedState { get; private set; }

    public uint Version { get; private set; }

    public bool HasUsableToken => TokenHash is not null && TokenConsumedAtUtc is null && TokenRevokedAtUtc is null;

    public void Hold() => DeliveryState = ProviderInteractionRecipientDeliveryState.HeldQuietHours;

    public void RecordSendSuccess(string tokenHash, string roomId, string? eventId, DateTimeOffset sentAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);

        TokenHash = tokenHash;
        RoomId = roomId;
        EventId = eventId;
        DeliveryState = ProviderInteractionRecipientDeliveryState.Sent;
        SentAtUtc = sentAtUtc;
        LastError = null;
    }

    public void RecordSendFailure(string? sanitizedError)
    {
        SendAttempts++;
        DeliveryState = ProviderInteractionRecipientDeliveryState.Failed;
        LastError = sanitizedError is null
            ? null
            : sanitizedError[..Math.Min(sanitizedError.Length, MaxLastErrorLength)];
    }

    public void MarkConsumed(DateTimeOffset atUtc) => TokenConsumedAtUtc = atUtc;

    public void RevokeToken(DateTimeOffset atUtc)
    {
        if (TokenConsumedAtUtc is not null)
        {
            return;
        }

        TokenRevokedAtUtc = atUtc;
    }

    public void MarkSkipped()
    {
        if (DeliveryState is ProviderInteractionRecipientDeliveryState.Pending
            or ProviderInteractionRecipientDeliveryState.HeldQuietHours)
        {
            DeliveryState = ProviderInteractionRecipientDeliveryState.Skipped;
        }
    }

    public void RecordRenderedState(string renderedState) => RenderedState = renderedState;

    public void ResetRenderedState() => RenderedState = null;
}

public enum ProviderInteractionAlertState
{
    Open,
    Claimed,
    Resolved,
    Superseded,
    Expired
}

public enum ProviderInteractionRecipientDeliveryState
{
    Pending,
    HeldQuietHours,
    Sent,
    Failed,
    Skipped
}

public static class ProviderInteractionAlertTransitions
{
    private static readonly Dictionary<ProviderInteractionAlertState, ProviderInteractionAlertState[]> Allowed = new()
    {
        [ProviderInteractionAlertState.Open] =
        [
            ProviderInteractionAlertState.Claimed,
            ProviderInteractionAlertState.Resolved,
            ProviderInteractionAlertState.Expired
        ],
        [ProviderInteractionAlertState.Claimed] =
        [
            ProviderInteractionAlertState.Resolved,
            ProviderInteractionAlertState.Superseded,
            ProviderInteractionAlertState.Expired
        ],
        [ProviderInteractionAlertState.Resolved] = [],
        [ProviderInteractionAlertState.Superseded] = [],
        [ProviderInteractionAlertState.Expired] = []
    };

    public static bool IsAllowed(ProviderInteractionAlertState from, ProviderInteractionAlertState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;
}

public sealed class InvalidProviderInteractionAlertTransitionException(
    ProviderInteractionAlertState from, ProviderInteractionAlertState to)
    : InvalidOperationException($"A provider interaction alert cannot move from {from} to {to}.")
{
    public ProviderInteractionAlertState From { get; } = from;

    public ProviderInteractionAlertState To { get; } = to;
}
