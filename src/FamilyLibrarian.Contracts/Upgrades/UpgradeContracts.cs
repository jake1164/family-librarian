namespace FamilyLibrarian.Contracts.Upgrades;

/// <summary>
/// A better copy of a delivered book (administrator pages only, so the title is shown). Otherwise ids, a provider id and evidence codes:
/// never a file name or text.
/// </summary>
public sealed record LibraryUpgradeResponse(
    Guid Id, Guid RequestId, string WorkTitle, string MediaType, string State, IReadOnlyList<string> Reasons, string ProviderId,
    string? FailureReason, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record LibraryUpgradeSettingsResponse(bool AutoReplace);

public sealed record SetLibraryUpgradeSettingsRequest(bool AutoReplace);
