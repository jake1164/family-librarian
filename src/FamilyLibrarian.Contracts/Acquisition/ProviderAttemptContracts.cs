namespace FamilyLibrarian.Contracts.Acquisition;

/// <summary>Administrative view of an append-only provider lookup record.</summary>
public sealed record ProviderAttemptResponse(
    Guid Id,
    Guid RequestFormatId,
    string ProviderId,
    string Outcome,
    string Summary,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? NextEligibleCheckAtUtc);

/// <summary>
/// Administrator summary of the search variants remembered for one requested format at one provider (AUTOFIND 2b): how many
/// distinct ways it was searched, when it was last searched, and which way found the candidate that was chosen.
/// </summary>
public sealed record SearchVariantSummaryResponse(
    Guid RequestFormatId,
    string ProviderId,
    int DistinctVariants,
    DateTimeOffset LastTriedAtUtc,
    string? FoundByKind,
    string Summary);
