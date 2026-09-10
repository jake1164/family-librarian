using FamilyLibrarian.Domain.Delivery;

namespace FamilyLibrarian.Application.Delivery;

/// <summary>
/// The admin Publishing Queue's read model for one Kindle delivery attempt.
/// </summary>
/// <remarks>
/// <see cref="RequestId"/>/<see cref="WorkId"/> are null for the existing-book
/// fast path, which has no <c>BookRequest</c> at all -- <see cref="WorkTitle"/>
/// still resolves there from the attempt's own denormalized book title (F9:
/// previously this fell back to a null request/work join and rendered as
/// "Existing library book" with no title at all). Requester identity is joined
/// here specifically because this is an administrator-only view (mirroring
/// <c>AdminBookRequestView</c>) -- the recipient's Kindle address itself is
/// never included; it lives only on <c>DeliveryTarget</c>, which this view
/// does not join.
/// </remarks>
public sealed record DeliveryAttemptView(
    Guid Id,
    Guid? RequestId,
    Guid? WorkId,
    string? WorkTitle,
    string RequesterDisplayName,
    string RequesterEmail,
    string ExternalBookId,
    DeliveryAttemptStatus Status,
    int AttemptNumber,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DeliveryConfirmationStatus ConfirmationStatus,
    DateTimeOffset? ConfirmedAtUtc);
