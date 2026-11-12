using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Matching;

/// <summary>
/// Removes the noise catalogs put around a person's name so every author
/// comparison sees "Jack Carr" for "Carr, Jack (Joint pseudonym)". Only
/// removes text and reorders a single comma; it never decides that two
/// different names are close, which stays <see cref="AuthorAffinity"/>'s job.
/// </summary>
internal static partial class AuthorNameForm
{
    internal static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal) { "JR", "SR", "II", "III", "IV" };

    /// <summary>Strips parenthetical qualifiers, then reorders "Last, First" to "First Last".</summary>
    internal static string Canonical(string name) => Reorder(StripQualifiers(name));

    /// <summary>
    /// Drops "(Joint pseudonym)", "(Editor)" and similar notes. An unclosed
    /// parenthesis cannot be paired with its note, so everything after it is dropped.
    /// </summary>
    internal static string StripQualifiers(string name)
    {
        var stripped = Qualifier().Replace(name, " ");
        var open = stripped.IndexOf('(');
        return (open < 0 ? stripped : stripped[..open]).Trim();
    }

    /// <summary>Reorders exactly one comma; "Smith, Jr." and "A, B, C" are not a Last, First pair.</summary>
    internal static string Reorder(string name)
    {
        var parts = name.Split(',');
        return parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0 &&
            !Suffixes.Contains(parts[1].Trim().TrimEnd('.').ToUpperInvariant())
            ? $"{parts[1].Trim()} {parts[0].Trim()}" : name;
    }

    [GeneratedRegex(@"\([^()]*\)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Qualifier();
}
