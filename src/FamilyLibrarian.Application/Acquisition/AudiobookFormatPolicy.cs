using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Deterministic automatic-acquisition ordering for audiobook containers and
/// codecs. This is a format compatibility policy, not a content-quality score.
/// </summary>
public static class AudiobookFormatPolicy
{
    /// <summary>Returns only candidates tied at the highest usable format.</summary>
    public static IReadOnlyList<FulfillmentOption> KeepHighestUsable(
        IEnumerable<FulfillmentOption> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var ranked = candidates
            .Select(candidate => (Candidate: candidate, Rank: Rank(candidate.Format)))
            .Where(entry => entry.Rank is not null)
            .ToArray();
        if (ranked.Length == 0)
        {
            return [];
        }

        var bestRank = ranked.Min(entry => entry.Rank!.Value);
        return ranked.Where(entry => entry.Rank == bestRank).Select(entry => entry.Candidate).ToArray();
    }

    public static bool IsUsableForAutomaticAcquisition(string? format) => Rank(format) is not null;

    /// <summary>Returns the neutral label used in an acquisition audit entry.</summary>
    public static string? GetAutomaticAcquisitionLabel(string? format) => Normalize(format) switch
    {
        "m4b" => "M4B",
        "mp3" => "MP3",
        "m4a" => "M4A",
        "aac" => "AAC",
        "opus" => "OPUS",
        "ogg" => "OGG",
        "oga" => "OGA",
        "flac" => "FLAC",
        _ => null
    };

    /// <summary>Builds the controlled provider-activity summary after acquisition.</summary>
    public static string DescribeAcquiredOption(FulfillmentOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var audiobookFormat = option.MediaType == RequestMediaType.Audiobook
            ? GetAutomaticAcquisitionLabel(option.Format)
            : null;
        return audiobookFormat is null
            ? "A high-confidence copy was acquired and sent through the security pipeline."
            : $"A high-confidence copy was acquired and sent through the security pipeline. Selected {audiobookFormat} under the automatic audiobook format policy.";
    }

    private static int? Rank(string? format) => Normalize(format) switch
    {
        "m4b" => 1,
        "mp3" => 2,
        "m4a" or "aac" => 3,
        "opus" => 4,
        "ogg" or "oga" => 5,
        "flac" => 6,
        _ => null
    };

    private static string? Normalize(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var trimmed = format.Trim();
        return trimmed[0] == '.' ? trimmed[1..].ToLowerInvariant() : trimmed.ToLowerInvariant();
    }
}
