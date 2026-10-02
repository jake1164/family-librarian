namespace FamilyLibrarian.Application.Providers;

/// <summary>
/// Derives the title sent to an acquisition source from a catalog title.
/// </summary>
/// <remarks>
/// Metadata providers often return a marketing title ("Threshing Day: Return to
/// the Empyrean world with thirteen stories...") that no release is named
/// after, so searching it verbatim finds nothing. This only widens what is
/// <em>searched</em>. Acceptance still verifies results against the full
/// catalog title and author (see <see cref="ExternalProviderMatchVerifier"/>),
/// so a shorter query can surface more candidates but never loosens what is
/// accepted.
/// </remarks>
public static class ExternalSearchTitle
{
    private static readonly string[] SubtitleSeparators = [":", " - ", " – ", " — ", " ("];

    public static string Build(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        var trimmed = title.Trim();
        var cut = SubtitleSeparators
            .Select(separator => trimmed.IndexOf(separator, StringComparison.Ordinal))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (cut < 0)
        {
            return trimmed;
        }

        var core = trimmed[..cut].Trim();
        var remainder = trimmed[cut..];

        // "Dune: Messiah" -> "Dune" would search a far broader, different
        // title, so a one-word core is only trusted when what it drops is a
        // long subtitle rather than a short distinguishing part of the title.
        var coreWords = core.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var remainderWords = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return coreWords >= 2 || remainderWords >= 4 ? core : trimmed;
    }
}
