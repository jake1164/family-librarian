namespace FamilyLibrarian.Contracts.Accounts;

/// <summary>
/// A household member's own quiet-hours window (HUMAN-ACQ-1 D9). All three
/// fields null together means the window is unset. <paramref name="IsAvailable"/>
/// is false while no email or Matrix delivery is switched on.
/// </summary>
public sealed record QuietHoursResponse(string? TimeZoneId, int? StartMinute, int? EndMinute, bool IsAvailable);

/// <summary>All three null clears the window; otherwise all three are required.</summary>
public sealed record SetQuietHoursRequest(string? TimeZoneId, int? StartMinute, int? EndMinute);
