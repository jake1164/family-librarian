using System.Globalization;
using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Providers;

/// <summary>A numbered release fragment, distinct from tracks inside a complete release.</summary>
public sealed partial record ExternalAudiobookPartEvidence(int Number, int? Total)
{
    public string Description => Total is { } total
        ? $"Part {Number} of {total}" : $"Part {Number} (total not reported)";

    public static ExternalAudiobookPartEvidence? Parse(string? releaseName) => Read(releaseName).Evidence;

    internal static (ExternalAudiobookPartEvidence? Evidence, string Name) Read(string? releaseName)
    {
        var name = releaseName ?? string.Empty;
        var matches = PartMarker().Matches(name);
        // Multiple markers or malformed numbering are not a reliable part identity.
        if (matches.Count != 1)
            return (null, name);
        var match = matches[0];
        var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        int? total = match.Groups["total"].Success
            ? int.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture) : null;
        if (number < 1 || total is < 1 || total < number)
            return (null, name);
        return (new(number, total), name.Remove(match.Index, match.Length));
    }

    // Dots, spaces and hyphens are source packaging. A bare "2" or "Book 2"
    // is not a split marker; it can be a catalog title/series position.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?:PART|PT|DISC|CD)[\W_]*(?<number>\d{1,2})(?:[.\s_-]*(?:OF|/)[.\s_-]*(?<total>\d{1,2}))?|(?<number>\d{1,2})[\W_]+OF[\W_]+(?<total>\d{1,2}))(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PartMarker();
}
