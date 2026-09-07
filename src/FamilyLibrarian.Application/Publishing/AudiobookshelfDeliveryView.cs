using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>One <see cref="AudiobookshelfDelivery"/> enriched with its owning Work/request context, for admin display.</summary>
public sealed record AudiobookshelfDeliveryView(
    Guid Id,
    Guid AssetId,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    string OriginalFilename,
    AudiobookshelfDeliveryStatus Status,
    string? ExternalItemId,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);
