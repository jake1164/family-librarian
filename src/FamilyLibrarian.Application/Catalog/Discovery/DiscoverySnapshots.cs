namespace FamilyLibrarian.Application.Catalog.Discovery;

/// <summary>
/// Defensive copies for the collection-bearing discovery contracts. A read-only interface is only a view: the caller who built
/// a list can keep mutating it, and a cast can mutate an array behind it. Every validated boundary stores a private copy
/// behind a <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>, so what was checked is what stays.
/// </summary>
internal static class DiscoverySnapshots
{
    public static IReadOnlyList<T> Of<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Array.AsReadOnly(source.ToArray());
    }
}
