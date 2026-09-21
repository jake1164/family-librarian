using System.Buffers.Binary;
using FamilyLibrarian.Domain.Acquisition;
using FamilyLibrarian.Domain.Requests;
using FamilyLibrarian.Infrastructure.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class MobiEncryptionValidatorTests
{
    [TestMethod]
    public async Task AnUnencryptedMobiFamilyFilePassesTheDrmCheck()
    {
        var outcome = await ValidateAsync(encryptionType: 0);

        Assert.IsTrue(outcome.IsValid, outcome.Message);
    }

    [TestMethod]
    public async Task AnEncryptedMobiFamilyFileIsRejected()
    {
        var outcome = await ValidateAsync(encryptionType: 2);

        Assert.IsFalse(outcome.IsValid);
        StringAssert.Contains(outcome.Message!, "encrypted");
    }

    private static async Task<FamilyLibrarian.Application.Security.ValidationOutcome> ValidateAsync(ushort encryptionType)
    {
        var bytes = new byte[128];
        "BOOKMOBI"u8.CopyTo(bytes.AsSpan(60));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(78, 4), 82);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(94, 2), encryptionType);

        var asset = new MediaAsset(
            Guid.NewGuid(), null, RequestMediaType.Ebook, ".azw3", "book.azw3", "book.azw3",
            bytes.Length, new string('a', 64), "application/x-mobipocket-ebook", Guid.NewGuid(), null,
            DateTimeOffset.UtcNow);

        return await new MobiEncryptionValidator().ValidateAsync(asset, new MemoryStream(bytes), CancellationToken.None);
    }
}
