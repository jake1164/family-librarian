namespace FamilyLibrarian.Application.Publishing;

internal static class PublishingFilenames
{
    /// <summary>A filesystem-safe filename derived from a title, never the caller-supplied original filename.</summary>
    public static string BuildTargetFilename(string title, string format)
    {
        var safeTitle = SafeTitle(title);
        return $"{safeTitle}-{Guid.NewGuid():N}.{format.TrimStart('.')}";
    }

    /// <summary>
    /// One track's filename within a multi-file bundle upload (e.g. a
    /// chaptered audiobook) — the leading zero-padded sequence keeps
    /// chapters in listing order at the destination independent of upload
    /// part order.
    /// </summary>
    public static string BuildBundleTrackFilename(string title, string format, int sequence)
    {
        var safeTitle = SafeTitle(title);
        return $"{safeTitle}-{sequence:000}-{Guid.NewGuid():N}.{format.TrimStart('.')}";
    }

    private static string SafeTitle(string title)
    {
        var safeTitle = string.Concat(title.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        return string.IsNullOrWhiteSpace(safeTitle) ? "book" : safeTitle.Trim();
    }
}
