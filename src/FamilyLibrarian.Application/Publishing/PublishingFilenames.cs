namespace FamilyLibrarian.Application.Publishing;

internal static class PublishingFilenames
{
    /// <summary>A filesystem-safe filename derived from a title, never the caller-supplied original filename.</summary>
    public static string BuildTargetFilename(string title, string format)
    {
        var safeTitle = string.Concat(title.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        safeTitle = string.IsNullOrWhiteSpace(safeTitle) ? "book" : safeTitle.Trim();
        return $"{safeTitle}-{Guid.NewGuid():N}.{format.TrimStart('.')}";
    }
}
