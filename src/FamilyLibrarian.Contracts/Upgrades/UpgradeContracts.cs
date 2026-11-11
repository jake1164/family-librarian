namespace FamilyLibrarian.Contracts.Upgrades;

/// <summary>A better copy of a delivered book. Ids, a provider id and evidence codes only: never a file name or text.</summary>
public sealed record LibraryUpgradeResponse(
    Guid Id, Guid RequestId, string State, IReadOnlyList<string> Reasons, string ProviderId, string? FailureReason,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
