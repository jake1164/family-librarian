using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Acquisition;

/// <summary>
/// M1-4b: identity attestation is explicit. A creation path that says nothing must not vouch for a file
/// it never examined.
/// </summary>
[TestClass]
public sealed class MediaAssetAttestationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);

    private static MediaAsset Create(bool? identityPreConfirmed = null) => identityPreConfirmed is { } value
        ? new(Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "a.epub", "s.epub", 1, new string('a', 64),
            "application/epub+zip", Guid.NewGuid(), null, Now, identityPreConfirmed: value)
        : new(Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "a.epub", "s.epub", 1, new string('a', 64),
            "application/epub+zip", Guid.NewGuid(), null, Now);

    [TestMethod]
    public void AnAssetConstructedWithoutTheFlagIsNotIdentityPreConfirmed() =>
        Assert.IsFalse(Create().IdentityPreConfirmed);

    [TestMethod]
    public void AnExplicitValueIsKeptExactly()
    {
        Assert.IsTrue(Create(true).IdentityPreConfirmed);
        Assert.IsFalse(Create(false).IdentityPreConfirmed);
    }
}
