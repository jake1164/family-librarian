using FamilyLibrarian.Contracts.Acquisition;
using FamilyLibrarian.Contracts.Publishing;
using FamilyLibrarian.Contracts.Requests;

namespace FamilyLibrarian.Contracts.Operations;

/// <summary>
/// Administrator-only operational snapshot. This is a read model over the
/// request, source, security, and publishing ledgers; it does not create a
/// separate background-job system or expose provider credentials.
/// </summary>
public sealed record AdminTasksResponse(
    DateTimeOffset GeneratedAtUtc,
    AdminTaskSummaryResponse Summary,
    IReadOnlyList<AdminBookRequestResponse> Requests,
    IReadOnlyList<AdminProviderTaskResponse> ProviderAttempts,
    IReadOnlyList<MediaAssetAdminResponse> SecurityActivity,
    IReadOnlyList<LibraryImportResponse> LibraryImports,
    IReadOnlyList<DeliveryResponse> Deliveries);

public sealed record AdminTaskSummaryResponse(
    int ActiveRequests,
    int NeedsReview,
    int SecurityAttention,
    int SourceAttention,
    int PublishingAttention);

/// <summary>One recent source lookup or download outcome, enriched only with
/// the request context an administrator needs to follow it.</summary>
public sealed record AdminProviderTaskResponse(
    Guid Id,
    Guid RequestId,
    string WorkTitle,
    string RequesterDisplayName,
    string MediaType,
    string ProviderId,
    string Outcome,
    string Summary,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? NextEligibleCheckAtUtc);
