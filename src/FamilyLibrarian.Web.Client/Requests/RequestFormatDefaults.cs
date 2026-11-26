using FamilyLibrarian.Contracts.Catalog;

namespace FamilyLibrarian.Web.Client.Requests;

/// <summary>Which format boxes a request form starts with.</summary>
public static class RequestFormatDefaults
{
    /// <summary>
    /// Applies the user's default, limited to formats that can be requested now.
    /// When none of the preferred formats can, the other one is offered instead.
    /// An unknown readiness (<paramref name="formats"/> null) counts as ready for
    /// the preferred formats, because the server enforces the real rules.
    /// </summary>
    public static (bool Ebook, bool Audiobook) Choose(string? preference, RequestFormatsResponse? formats)
    {
        var ebookReady = formats?.Ebook is not { IsReady: false };
        var audiobookReady = formats?.Audiobook is not { IsReady: false };

        var ebook = preference != "Audiobook" && ebookReady;
        var audiobook = preference is "Audiobook" or "Both" && audiobookReady;
        if (ebook || audiobook)
        {
            return (ebook, audiobook);
        }

        // Never preselect an unknown format the user did not ask for.
        return ebookReady ? (true, false) : (false, formats?.Audiobook is { IsReady: true });
    }
}
