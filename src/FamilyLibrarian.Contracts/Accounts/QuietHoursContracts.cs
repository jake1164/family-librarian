namespace FamilyLibrarian.Contracts.Accounts;

/// <summary>
/// A household member's own quiet-hours window (HUMAN-ACQ-1 D9). All three
/// fields null together means the window is unset.
/// </summary>
public sealed record QuietHoursResponse(string? TimeZoneId, int? StartMinute, int? EndMinute);

/// <summary>All three null clears the window; otherwise all three are required.</summary>
public sealed record SetQuietHoursRequest(string? TimeZoneId, int? StartMinute, int? EndMinute);
