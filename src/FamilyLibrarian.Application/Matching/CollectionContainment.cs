using System.Globalization;
using System.Text.RegularExpressions;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Application.Matching;

public enum ContainmentVerdict { Contains = 1, DoesNotContain = 2, Unknown = 3 }

public static class CollectionContainment
{
    private static readonly Regex Range = new(@"^(?:BOOKS?\s*|#\s*|VOL\.?\s*)?(\d+(?:\.\d+)?)\s*[-–]\s*(\d+(?:\.\d+)?)(?:\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static ContainmentVerdict Evaluate(BookIdentity requested, ExternalProviderCandidate candidate)
    {
        foreach (var title in new[] { candidate.Title, candidate.Release?.Name }.OfType<string>())
        {
            var text = title[..Math.Min(title.Length, 4096)];
            foreach (var series in requested.Series ?? [])
            {
                if (string.IsNullOrWhiteSpace(series.Name) ||
                    !decimal.TryParse(series.Position, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var position)) continue;
                var marker = text.IndexOf(series.Name, StringComparison.OrdinalIgnoreCase);
                if (marker < 0 || marker > 0 && char.IsLetterOrDigit(text[marker - 1])) continue;
                var match = Range.Match(text[(marker + series.Name.Length)..].TrimStart(' ', ':', ',', '-'));
                if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var first) ||
                    !decimal.TryParse(match.Groups[2].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var last) || first > last) continue;
                return position >= first && position <= last ? ContainmentVerdict.Contains : ContainmentVerdict.DoesNotContain;
            }
        }
        foreach (var title in new[] { candidate.Title, candidate.Release?.Name }.OfType<string>())
        {
            var parts = title[..Math.Min(title.Length, 4096)].Split([" / ", " & ", "; ", ", "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && parts.Any(part => ReleaseTitleMatcher.Tokens(requested.Title).SequenceEqual(ReleaseTitleMatcher.Tokens(part))))
                return ContainmentVerdict.Contains;
        }
        return ContainmentVerdict.Unknown;
    }
}
