using FamilyLibrarian.Domain.Acquisition;

namespace FamilyLibrarian.Application.Acquisition;

/// <summary>
/// Where a staged asset came from and what, if anything, vouched for its identity, decided by the code path
/// that actually creates the asset (alpha.6 M1-5a).
/// </summary>
/// <remarks>
/// Only these factories can build one, so a creation site must name its source and the type system keeps
/// privileged provenance out of reach of anything that merely carries a provider id or name. A built-in
/// provider's id, the word "manual", or an external provider that happens to share either, confer nothing:
/// <see cref="ManualUpload"/> needs an authenticated user id (taken from the authorization context by
/// <see cref="ManualImportService"/>), <see cref="BuiltInProvider"/> is passed only by the built-in
/// provider fetch, and <see cref="ExternalProvider"/> carries the job's own confirmation value.
/// </remarks>
public sealed record AssetProvenance
{
    private AssetProvenance(AssetOrigin origin, bool identityPreConfirmed, Guid? userId)
    {
        Origin = origin;
        IdentityPreConfirmed = identityPreConfirmed;
        UserId = userId;
    }

    public AssetOrigin Origin { get; }

    /// <summary>A statement about the source's pre-download matching. Never content verification.</summary>
    public bool IdentityPreConfirmed { get; }

    /// <summary>The authenticated administrator behind a manual upload; null for every other origin.</summary>
    public Guid? UserId { get; }

    public static AssetProvenance ManualUpload(Guid administratorUserId)
    {
        if (administratorUserId == Guid.Empty)
        {
            throw new ArgumentException("A manual upload needs the authenticated administrator's id.", nameof(administratorUserId));
        }

        return new(AssetOrigin.ManualUpload, identityPreConfirmed: true, administratorUserId);
    }

    public static AssetProvenance BuiltInProvider() => new(AssetOrigin.BuiltInProvider, identityPreConfirmed: true, userId: null);

    public static AssetProvenance ExternalProvider(bool identityPreConfirmed) =>
        new(AssetOrigin.ExternalProvider, identityPreConfirmed, userId: null);
}
