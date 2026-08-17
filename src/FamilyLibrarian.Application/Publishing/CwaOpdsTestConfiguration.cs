namespace FamilyLibrarian.Application.Publishing;

/// <summary>Unsaved OPDS values used for a one-off catalog-search probe.</summary>
public sealed record CwaOpdsTestConfiguration(
    string? OpdsBaseUrl,
    string? OpdsUsername,
    string? OpdsPassword);
