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
