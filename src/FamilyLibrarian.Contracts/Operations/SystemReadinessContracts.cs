namespace FamilyLibrarian.Contracts.Operations;

/// <summary>
/// The plain, everyone-visible signal behind the app's status footer. Never
/// carries which source is degraded or why — that detail is an admin-only
/// concern surfaced on the Tasks page instead.
/// </summary>
public sealed record SystemReadinessResponse(bool Healthy);
