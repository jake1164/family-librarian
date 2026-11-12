using System.Globalization;
using System.Text.RegularExpressions;

namespace FamilyLibrarian.Application.Security;

/// <summary>An audiobook title tag with its packaging removed, and whether it named a dramatized adaptation.</summary>
public sealed record AudioPackaging(string CoreTitle, bool IsDramatized);

/// <summary>
/// Packaging that an audiobook's title tag carries but that is not part of the book's title. On the lab both parts of an
/// Onyx Storm set were tagged "Onyx Storm (1 of 2) [Dramatized Adaptation]" and held as "the wrong title".
/// </summary>
/// <remarks>
/// <para>A part marker ("(1 of 2)", "[Part 2 of 3]", "(Disc 1/2)") is removed only when its total equals the size of the
/// set the file arrived in. A lone file saying "1 of 2" is a fragment, and must not pass as the whole book.</para>
/// <para>A bracketed dramatization marker ("[Dramatized Adaptation]", "(Full Cast Dramatization)") is removed from the
/// title so the title can be compared, and reported: a dramatization is a different production from the narrated
/// audiobook, so it is kept in reserve rather than accepted. Only bracketed markers count, so a book whose real title
/// contains the word is not affected.</para>
/// </remarks>
public static partial class AudioPackagingMarkers
{
    public static AudioPackaging Read(string title, int? setSize)
    {
        ArgumentNullException.ThrowIfNull(title);
        var dramatized = Dramatization().IsMatch(title);
        var core = Dramatization().Replace(title, " ");
        if (setSize is > 1)
        {
            core = PartOfSet().Replace(core, match =>
                int.TryParse(match.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total) &&
                total == setSize
                    ? " "
                    : match.Value);
        }

        core = Spaces().Replace(core, " ").Trim().TrimEnd('-', ':', ',', '–', '—').Trim();
        return new AudioPackaging(core.Length == 0 ? title.Trim() : core, dramatized);
    }

    [GeneratedRegex(
        @"[(\[]\s*(?:full[\s-]*cast\s+)?(?:dramati[sz]ed(?:\s+adaptation)?|dramati[sz]ation|audio\s+drama|full[\s-]*cast(?:\s+(?:recording|production|edition))?)\s*[)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Dramatization();

    [GeneratedRegex(
        @"[(\[]\s*(?:(?:part|disc|disk|cd)\s*)?\d{1,3}\s*(?:of|/)\s*(?<total>\d{1,3})\s*[)\]]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PartOfSet();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Spaces();
}
