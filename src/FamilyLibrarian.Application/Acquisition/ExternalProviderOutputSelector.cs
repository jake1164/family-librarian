using System.Globalization;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>Chooses a complete media set from an untrusted protocol-v2 output listing.</summary>
public static class ExternalProviderOutputSelector
{
    public static IReadOnlyList<ExternalProviderOutput> Select(
        IReadOnlyList<ExternalProviderOutput> outputs,
        RequestMediaType mediaType,
        ManualImportPolicy importPolicy,
        ExternalProviderOutputPolicy outputPolicy)
    {
        if (outputs.Count == 0)
            throw new InvalidExternalProviderOutputException("The provider returned no outputs.");
        if (outputs.Count > outputPolicy.MaxOutputCount)
            throw new InvalidExternalProviderOutputException("The provider returned more outputs than Family Librarian permits.");
        if (outputs.Any(output => string.IsNullOrWhiteSpace(output.OutputId)) ||
            outputs.Select(output => output.OutputId).Distinct(StringComparer.Ordinal).Count() != outputs.Count)
            throw new InvalidExternalProviderOutputException("The provider returned a missing or duplicate output ID.");
        if (outputs.Any(output => output.Kind == ProviderOutputKind.Unknown))
            throw new InvalidExternalProviderOutputException("The provider returned an output with an unknown kind.");
        if (outputs.Any(output => output.Sequence is not null &&
            (output.Kind != ProviderOutputKind.File || !IsRole(output.Role, "audio-part"))))
            throw new InvalidExternalProviderOutputException("The provider returned a sequence on a non-audio-part file output.");

        var possibleMedia = outputs.Where(output =>
        {
            if (output.Kind != ProviderOutputKind.File)
                return false;
            var role = output.Role?.Trim();
            if (IsSidecarRole(role))
                return false;
            var extension = Path.GetExtension(NormalizeFilename(output.Filename));
            return !string.IsNullOrEmpty(extension) && importPolicy.IsExtensionAllowed(mediaType, extension);
        }).ToArray();

        if (possibleMedia.Length == 0)
            throw new InvalidExternalProviderOutputException($"The provider returned no permitted {mediaType} file output.");

        if (mediaType == RequestMediaType.Ebook)
        {
            var selected = possibleMedia.Where(output => IsRole(output.Role, "ebook") || IsRole(output.Role, "primary") ||
                string.IsNullOrWhiteSpace(output.Role)).ToArray();
            if (selected.Length == 0 && possibleMedia.Length == 1 &&
                !IsSidecarRole(possibleMedia[0].Role) && !IsRole(possibleMedia[0].Role, "audio-part"))
                return possibleMedia;
            if (selected.Length != 1 || possibleMedia.Length != 1)
                throw new InvalidExternalProviderOutputException("The provider returned an ambiguous ebook output set.");
            return selected;
        }

        var audioParts = possibleMedia.Where(output => IsRole(output.Role, "audio-part")).ToArray();
        var primaries = possibleMedia.Where(output => IsRole(output.Role, "primary")).ToArray();
        if (audioParts.Length > 0 && primaries.Length > 0)
            throw new InvalidExternalProviderOutputException("The provider mixed a primary audiobook with audio-part outputs.");
        if (audioParts.Length == 0)
        {
            var role = possibleMedia.Length == 1 ? possibleMedia[0].Role : null;
            if (possibleMedia.Length != 1 || IsRole(role, "ebook") || IsSidecarRole(role))
                throw new InvalidExternalProviderOutputException("The provider returned an ambiguous audiobook output set.");
            return possibleMedia;
        }

        if (audioParts.Length != possibleMedia.Length)
            throw new InvalidExternalProviderOutputException("The provider mixed audiobook tracks with other media files.");
        return OrderAudioParts(audioParts);
    }

    public static string NormalizeFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return string.Empty;
        var normalized = filename.Replace('\\', '/');
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    private static ExternalProviderOutput[] OrderAudioParts(ExternalProviderOutput[] outputs)
    {
        if (outputs.All(output => output.Sequence is > 0))
        {
            var ordered = outputs.OrderBy(output => output.Sequence).ToArray();
            if (ordered.Select((output, index) => output.Sequence == index + 1).All(isContiguous => isContiguous))
                return ordered;
            throw new InvalidExternalProviderOutputException("The provider returned invalid audio-part sequence values.");
        }

        if (outputs.Any(output => output.Sequence is not null))
            throw new InvalidExternalProviderOutputException("The provider returned an incomplete audio-part sequence.");

        var legacy = outputs.Select(output => (Output: output, Number: TryGetLegacyTrackNumber(output.Filename))).ToArray();
        if (legacy.Any(item => item.Number is null))
            throw new InvalidExternalProviderOutputException("The provider did not provide a safe order for all audiobook tracks.");
        var sorted = legacy.OrderBy(item => item.Number).ToArray();
        if (sorted.Select((item, index) => item.Number == index + 1).Any(isNotContiguous => !isNotContiguous))
            throw new InvalidExternalProviderOutputException("The provider's numbered audiobook filenames do not form a complete sequence.");
        return sorted.Select(item => item.Output).ToArray();
    }

    private static int? TryGetLegacyTrackNumber(string? filename)
    {
        var stem = Path.GetFileNameWithoutExtension(NormalizeFilename(filename)) ?? string.Empty;
        var digitCount = 0;
        while (digitCount < stem.Length && char.IsAsciiDigit(stem[digitCount]))
            digitCount++;
        if (digitCount == 0 || digitCount >= stem.Length - 1 ||
            stem[digitCount] is not ('-' or '_') ||
            char.IsWhiteSpace(stem[digitCount + 1]) ||
            stem[digitCount + 1] is '-' or '_')
            return null;
        return int.TryParse(stem[..digitCount], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    private static bool IsSidecarRole(string? role) => role is not null &&
        (role.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
         role.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
         role.Equals("checksum", StringComparison.OrdinalIgnoreCase) ||
         role.Equals("descriptor", StringComparison.OrdinalIgnoreCase) ||
         role.Equals("archive", StringComparison.OrdinalIgnoreCase) ||
         role.Equals("supplementary", StringComparison.OrdinalIgnoreCase) ||
         role.Equals("other", StringComparison.OrdinalIgnoreCase));

    private static bool IsRole(string? role, string expected) =>
        string.Equals(role?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}

public sealed class InvalidExternalProviderOutputException(string message) : InvalidOperationException(message);
