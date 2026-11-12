namespace FamilyLibrarian.Domain.Acquisition;

/// <summary>
/// The concrete acquisition source of a <see cref="MediaAsset"/>, fixed at the moment the asset is
/// created and never changed afterwards (alpha.6 M1-5a).
/// </summary>
/// <remarks>
/// Established by the code path that actually creates the asset: the administrator-only manual upload
/// route, a built-in provider's fetch, or an external provider's job. It is <b>never</b> derived from a
/// provider id or name, which an external provider can choose freely. The value is a statement about the
/// source, not about the file's content.
/// </remarks>
public enum AssetOrigin
{
    /// <summary>
    /// A row that existed before origin was recorded. Explicit, not guessed: nothing is inferred about who
    /// supplied it, and it keeps the behavior it always had. Never assigned to a new asset.
    /// </summary>
    Legacy = 1,

    /// <summary>A creation path that did not say where the asset came from. Fails closed: it attests nothing.</summary>
    Unknown = 2,

    /// <summary>
    /// Uploaded through the manual-import service by an authenticated administrator;
    /// <see cref="MediaAsset.OriginUserId"/> is that administrator, taken from the authenticated context.
    /// </summary>
    ManualUpload = 3,

    /// <summary>Fetched by a built-in provider compiled into Family Librarian (Project Gutenberg, LibriVox).</summary>
    BuiltInProvider = 4,

    /// <summary>Delivered by an administrator-registered external provider (unvetted third-party code).</summary>
    ExternalProvider = 5
}
