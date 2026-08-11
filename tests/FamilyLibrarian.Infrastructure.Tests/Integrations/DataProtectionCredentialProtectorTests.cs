using FamilyLibrarian.Infrastructure.Integrations;
using Microsoft.AspNetCore.DataProtection;

namespace FamilyLibrarian.Infrastructure.Tests.Integrations;

[TestClass]
public sealed class DataProtectionCredentialProtectorTests
{
    private const string Provider = "googlebooks";

    [TestMethod]
    public void ProtectThenUnprotectReturnsTheOriginalValue()
    {
        var protector = CreateProtector();

        var ciphertext = protector.Protect(Provider, "google-books-api-key");

        Assert.AreNotEqual("google-books-api-key", ciphertext);
        Assert.AreEqual(
            "google-books-api-key",
            protector.Unprotect(Provider, ciphertext, protector.FormatVersion));
    }

    [TestMethod]
    public void AValueProtectedForOneProviderCannotBeUnprotectedAsAnother()
    {
        // Purpose separation per provider: a mis-set row or swapped column must
        // not leak one integration's key into another's outbound requests.
        var protector = CreateProtector();

        var ciphertext = protector.Protect(Provider, "google-books-api-key");

        Assert.IsNull(protector.Unprotect("openlibrary", ciphertext, protector.FormatVersion));
    }

    [TestMethod]
    public void TheProviderIdIsMatchedCaseInsensitively()
    {
        var protector = CreateProtector();

        var ciphertext = protector.Protect("GoogleBooks", "google-books-api-key");

        Assert.AreEqual(
            "google-books-api-key",
            protector.Unprotect("googlebooks", ciphertext, protector.FormatVersion));
    }

    [TestMethod]
    public void UnprotectRejectsAnUnknownFormatVersion()
    {
        var protector = CreateProtector();
        var ciphertext = protector.Protect(Provider, "google-books-api-key");

        Assert.IsNull(protector.Unprotect(Provider, ciphertext, protector.FormatVersion + 1));
    }

    [TestMethod]
    public void UnprotectReturnsNullWhenTheKeyRingCannotReadTheValue()
    {
        // A different key ring stands in for a lost or rotated one. The provider
        // must report "not configured" rather than throwing on every search.
        var written = CreateProtector();
        var other = CreateProtector();

        var ciphertext = written.Protect(Provider, "google-books-api-key");

        Assert.IsNull(other.Unprotect(Provider, ciphertext, other.FormatVersion));
    }

    [TestMethod]
    public void UnprotectReturnsNullForGarbage()
    {
        var protector = CreateProtector();

        Assert.IsNull(protector.Unprotect(Provider, "not-a-protected-value", protector.FormatVersion));
    }

    private static DataProtectionCredentialProtector CreateProtector() =>
        new(DataProtectionProvider.Create(
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()))));
}
