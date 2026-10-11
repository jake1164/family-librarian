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

    private static MediaAsset WithOrigin(AssetOrigin origin, Guid? userId = null) =>
        new(Guid.NewGuid(), null, RequestMediaType.Ebook, ".epub", "a.epub", "s.epub", 1, new string('a', 64),
            "application/epub+zip", Guid.NewGuid(), null, Now, identityPreConfirmed: true, origin: origin, originUserId: userId);

    [TestMethod]
    public void AnAssetWithoutAnOriginFailsClosedAsUnknown()
    {
        var asset = Create();
        Assert.AreEqual(AssetOrigin.Unknown, asset.Origin);
        Assert.IsNull(asset.OriginUserId);
        Assert.IsNull(asset.IdentityHoldCode);
    }

    [TestMethod]
    public void AManualUploadRecordsItsAuthenticatedUserAndOnlyAManualUploadMay()
    {
        var user = Guid.NewGuid();
        var manual = WithOrigin(AssetOrigin.ManualUpload, user);
        Assert.AreEqual(user, manual.OriginUserId);

        Assert.Throws<ArgumentException>(() => WithOrigin(AssetOrigin.ManualUpload));
        Assert.Throws<ArgumentException>(() => WithOrigin(AssetOrigin.BuiltInProvider, user));
        Assert.Throws<ArgumentException>(() => WithOrigin(AssetOrigin.ExternalProvider, user));
        Assert.Throws<ArgumentException>(() => WithOrigin(AssetOrigin.Unknown, user));
    }

    [TestMethod]
    public void LegacyIsReservedForRowsThatPredateOriginAndCannotBeAssignedToANewAsset()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WithOrigin(AssetOrigin.Legacy));
        Assert.Throws<ArgumentOutOfRangeException>(() => WithOrigin((AssetOrigin)99));
    }

    [TestMethod]
    public void AnUnsetOriginValueIsRefusedAndNeverBecomesLegacy()
    {
        // 0 is what an uninitialised enum holds. It must not be accepted, and nothing may translate it to 'Legacy'.
        Assert.Throws<ArgumentOutOfRangeException>(() => WithOrigin(default));
    }

    [TestMethod]
    public void ARowMaterialisedWithoutAStoredOriginDefaultsToUnknownNotLegacy()
    {
        // The private constructor is what the persistence layer uses before it assigns stored values.
        var materialised = (MediaAsset)Activator.CreateInstance(typeof(MediaAsset), nonPublic: true)!;

        Assert.AreEqual(AssetOrigin.Unknown, materialised.Origin, "An origin that was never set fails closed; it is not trusted legacy provenance.");
    }

    [TestMethod]
    public void OriginHasNoPublicWayToChangeAfterCreation()
    {
        foreach (var name in new[] { nameof(MediaAsset.Origin), nameof(MediaAsset.OriginUserId) })
        {
            var property = typeof(MediaAsset).GetProperty(name)!;
            Assert.IsTrue(property.SetMethod is null || !property.SetMethod.IsPublic, $"{name} must not have a public setter.");
        }

        var mutators = typeof(MediaAsset).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(AssetOrigin)))
            .Select(method => method.Name)
            .ToArray();
        Assert.IsEmpty(mutators, "No public method may take an origin: " + string.Join(", ", mutators));
    }

    [TestMethod]
    public void AHoldCodeIsSetAndClearedAndBlankMeansCleared()
    {
        var asset = Create();
        asset.SetIdentityHoldCode(" identity.hold.language_conflict ");
        Assert.AreEqual("identity.hold.language_conflict", asset.IdentityHoldCode);
        asset.SetIdentityHoldCode("  ");
        Assert.IsNull(asset.IdentityHoldCode);
    }

    [TestMethod]
    public void AnExplicitValueIsKeptExactly()
    {
        Assert.IsTrue(Create(true).IdentityPreConfirmed);
        Assert.IsFalse(Create(false).IdentityPreConfirmed);
    }
}
