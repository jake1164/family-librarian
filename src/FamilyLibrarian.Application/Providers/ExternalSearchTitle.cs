using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Derives the title sent to an acquisition source from a catalog title.
/// </summary>
/// <remarks>
/// Metadata providers often return a marketing title ("Threshing Day: Return to
/// the Empyrean world with thirteen stories...") that no release is named
/// after, so searching it verbatim finds nothing.
/// <para>
/// This is deliberately the same reduction acceptance uses
/// (<see cref="WorkTitleCore"/>). It used to be a second, private copy of the
/// rule, which made the two halves work against each other: the search widened
/// to "Moby Dick" while <see cref="ExternalProviderMatchVerifier"/> still
/// compared against "Moby Dick (Illustrated Classics)", so widening the query
/// only surfaced candidates that verification was then guaranteed to reject.
/// </para>
/// <para>
/// Widening the query still never loosens what is <em>accepted</em>:
/// acceptance continues to require author agreement and to rank a reduced-title
/// match below an exact one.
/// </para>
/// </remarks>
public static class ExternalSearchTitle
{
    public static string Build(string title) => WorkTitleCore.Reduce(title);
}
