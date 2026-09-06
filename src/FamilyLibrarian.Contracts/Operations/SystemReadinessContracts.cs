namespace FamilyLibrarian.Contracts.Operations;

/// <summary>
/// The plain, everyone-visible signal behind the app's status footer, plus
/// enough structure for an admin viewer to see which category and component
/// is behind a "Degraded" reading without digging through the Sources or
/// Publishing pages. A non-admin viewer only ever sees <see cref="Healthy"/>
/// and a generic message built from it -- the client decides how much of
/// <see cref="DegradedComponents"/> to show based on the caller's role.
/// </summary>
public sealed record SystemReadinessResponse(
    bool Healthy,
    IReadOnlyList<DegradedSystemComponentResponse> DegradedComponents);

/// <summary>One enabled source or publishing destination currently counted against <see cref="SystemReadinessResponse.Healthy"/>.</summary>
public sealed record DegradedSystemComponentResponse(string Category, string Name, string? Detail);

public static class SystemReadinessCategories
{
    public const string Source = "Source";
    public const string Publishing = "Publishing";
}
