using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Application.Publishing;

/// <summary>
/// Builds the deep link to a specific book/item's page on a linked library
/// destination, shared by every caller that needs to turn a published id back
/// into something a family member's browser can open.
/// </summary>
public static class ExternalLibraryLinks
{
    // settings.PublicUrl, when set, is what a family member's browser can
    // actually reach -- settings.OpdsBaseUrl is only guaranteed reachable by
    // Family Librarian's own backend (in a containerized deployment it is
    // routinely a Docker-internal hostname like http://cwa:8083).
    public static Uri? BuildCwaBookLink(CwaSettings? settings, string? bookId)
    {
        if (settings is null || !settings.IsEnabled || string.IsNullOrWhiteSpace(bookId))
        {
            return null;
        }

        var baseUrl = settings.PublicUrl ?? settings.OpdsBaseUrl;
        return BuildLink(baseUrl, "book", bookId);
    }

    // settings.PublicUrl, when set, is what a family member's browser can
    // actually reach -- settings.BaseUrl is only guaranteed reachable by
    // Family Librarian's own backend (in a containerized deployment it is
    // routinely a Docker-internal hostname like http://abs:80).
    public static Uri? BuildAudiobookshelfItemLink(AudiobookshelfSettings? settings, string? itemId)
    {
        if (settings is null || !settings.IsEnabled || string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        var baseUrl = settings.PublicUrl ?? settings.BaseUrl;
        return BuildLink(baseUrl, "item", itemId);
    }

    private static Uri? BuildLink(string? baseUrl, string segment, string id) =>
        !string.IsNullOrWhiteSpace(baseUrl) &&
        Uri.TryCreate($"{baseUrl.TrimEnd('/')}/{segment}/{id}", UriKind.Absolute, out var uri)
            ? uri
            : null;
}
