using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>One <see cref="Delivery"/> enriched with its owning Work/request context, for admin display.</summary>
public sealed record DeliveryView(
    Guid Id,
    Guid AssetId,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    string OriginalFilename,
    DeliveryStatus Status,
    string? ExternalItemId,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);
