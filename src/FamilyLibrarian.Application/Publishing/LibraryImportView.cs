using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>One <see cref="LibraryImport"/> enriched with its owning Work/request context, for admin display.</summary>
public sealed record LibraryImportView(
    Guid Id,
    Guid AssetId,
    Guid RequestId,
    Guid WorkId,
    string WorkTitle,
    string OriginalFilename,
    LibraryImportStatus Status,
    string? ExternalBookId,
    string? FailureReason,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc);
