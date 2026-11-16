using System.Text;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Domain.Tests.Catalog;

/// <summary>
/// The accepted evidence budgets are UTF-8 byte budgets (16 KiB per claim value, 64 KiB per stored snapshot), not character counts:
/// a multibyte string that is short in characters can be long in bytes, and the domain must refuse it either way.
/// </summary>
[TestClass]
public sealed class EvidenceByteBudgetTests
{
    private static readonly DateTimeOffset Seen = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static MetadataClaim ClaimOf(string valueJson) =>
        new(Guid.NewGuid(), MetadataClaimField.Description, "work", "demo", "r1", valueJson, "hash", Seen);

    private static SourceObservation SnapshotOf(string snapshot) =>
        new(Guid.NewGuid(), "demo", "r1", "hash", snapshot, 1, Seen);

    /// <summary>A JSON object whose UTF-8 size is exactly <paramref name="bytes"/>, padded with a three-byte character.</summary>
    private static string ObjectOfExactly(int bytes)
    {
        const string prefix = "{\"text\":\"";
        const string suffix = "\"}";
        var fixedBytes = Encoding.UTF8.GetByteCount(prefix + suffix);
        var body = bytes - fixedBytes;
        var text = new string('界', body / 3) + new string('a', body % 3);
        var json = prefix + text + suffix;
        Assert.AreEqual(bytes, Encoding.UTF8.GetByteCount(json));
        return json;
    }

    [TestMethod]
    public void AClaimValueMayBeExactlySixteenKibibytesOfUtf8ButNotOneByteMore()
    {
        var exact = ObjectOfExactly(16 * 1024);

        Assert.IsNotNull(ClaimOf(exact));
        Assert.Throws<ArgumentException>(() => ClaimOf(ObjectOfExactly(16 * 1024 + 1)));
    }

    [TestMethod]
    public void AShortInCharactersClaimValueThatIsLongInBytesIsRefused()
    {
        var multibyte = ObjectOfExactly(18_000);

        Assert.IsLessThan(16 * 1024, multibyte.Length, "Fewer than 16 K characters, so a character count would have let it through.");
        Assert.Throws<ArgumentException>(() => ClaimOf(multibyte));
    }

    [TestMethod]
    public void ASnapshotMayBeExactlySixtyFourKibibytesOfUtf8ButNotOneByteMore()
    {
        Assert.IsNotNull(SnapshotOf(ObjectOfExactly(64 * 1024)));
        Assert.Throws<ArgumentException>(() => SnapshotOf(ObjectOfExactly(64 * 1024 + 1)));
        Assert.Throws<ArgumentException>(() => SnapshotOf(ObjectOfExactly(70_000)), "Under 64 K characters, over 64 KiB.");
    }
}
