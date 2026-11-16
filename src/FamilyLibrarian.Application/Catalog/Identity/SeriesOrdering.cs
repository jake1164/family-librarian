using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Catalog.Identity;

/// <summary>
/// What kind of ordering a series position belongs to, read only from an explicit qualifier in the source's own series label
/// ("The Expanse (Chronological)"). An unqualified label is <see cref="SeriesOrderingKind.Unspecified"/> and is never assumed to be
/// publication order.
/// </summary>
public static partial class SeriesOrdering
{
    public static SeriesOrderingKind FromLabel(string? seriesLabel)
    {
        if (string.IsNullOrWhiteSpace(seriesLabel))
        {
            return SeriesOrderingKind.Unspecified;
        }

        var qualifier = Qualifier().Matches(seriesLabel).Select(match => match.Groups[1].Value.ToLowerInvariant()).ToArray();
        foreach (var text in qualifier)
        {
            if (text.Contains("chronolog", StringComparison.Ordinal) || text.Contains("timeline", StringComparison.Ordinal))
            {
                return SeriesOrderingKind.Chronological;
            }

            if (text.Contains("reading", StringComparison.Ordinal))
            {
                return SeriesOrderingKind.Reading;
            }

            if (text.Contains("publication", StringComparison.Ordinal) || text.Contains("release", StringComparison.Ordinal))
            {
                return SeriesOrderingKind.Publication;
            }
        }

        return SeriesOrderingKind.Unspecified;
    }

    /// <summary>The ordering a candidate states: its own field when a provider set one, else the label's explicit qualifier.</summary>
    public static SeriesOrderingKind Of(BookSeriesCandidate series) => series.OrderingKind ?? FromLabel(series.Name);

    [GeneratedRegex(@"\(([^)]*)\)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Qualifier();
}
