using FamilyLibrarian.Domain.Audit;
using FamilyLibrarian.Domain.Integrations;

namespace FamilyLibrarian.Domain.Tests.Integrations;

[TestClass]
public sealed class MetadataProviderSettingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ANewSettingStartsDisabledWithNoCredential()
    {
        var setting = new MetadataProviderSetting("googlebooks", Now);

        Assert.IsFalse(setting.IsEnabled);
        Assert.IsFalse(setting.HasStoredCredential);
        Assert.IsNull(setting.CredentialSetAtUtc);
    }

    [TestMethod]
    public void SettingACredentialRecordsTheFormatVersionAndHint()
    {
        var setting = new MetadataProviderSetting("googlebooks", Now);

        setting.SetCredential("ciphertext", credentialFormatVersion: 1, "alue", actorUserId: null, Now);

        Assert.IsTrue(setting.HasStoredCredential);
        Assert.AreEqual(1, setting.CredentialFormatVersion);
        Assert.AreEqual("alue", setting.CredentialHint);
        Assert.AreEqual(Now, setting.CredentialSetAtUtc);
    }

    [TestMethod]
    public void ReplacingACredentialClearsTheStaleTestResult()
    {
        var setting = new MetadataProviderSetting("googlebooks", Now);
        setting.SetCredential("ciphertext", 1, "abcd", null, Now);
        setting.RecordTestResult(true, "Connected.", null, Now);

        setting.SetCredential("new-ciphertext", 1, "wxyz", null, Now.AddMinutes(1));

        // A result proven against the old key says nothing about the new one.
        Assert.IsNull(setting.LastTestedAtUtc);
        Assert.IsNull(setting.LastTestSucceeded);
        Assert.IsNull(setting.LastTestMessage);
    }

    [TestMethod]
    public void ClearingACredentialResetsEveryCredentialField()
    {
        var setting = new MetadataProviderSetting("googlebooks", Now);
        setting.SetCredential("ciphertext", 1, "abcd", null, Now);

        setting.ClearCredential(null, Now.AddMinutes(1));

        Assert.IsFalse(setting.HasStoredCredential);
        Assert.IsNull(setting.CredentialHint);
        Assert.IsNull(setting.CredentialSetAtUtc);
        Assert.AreEqual(0, setting.CredentialFormatVersion);
    }

    [TestMethod]
    public void SetCredentialRejectsAnEmptyValue()
    {
        var setting = new MetadataProviderSetting("googlebooks", Now);

        Assert.ThrowsExactly<ArgumentException>(() =>
            setting.SetCredential("   ", 1, null, null, Now));
    }

    [TestMethod]
    public void ALongTestMessageIsTruncatedRatherThanOverflowingTheColumn()
    {
        var setting = new MetadataProviderSetting("googlebooks", Now);

        setting.RecordTestResult(false, new string('x', 900), null, Now);

        Assert.AreEqual(512, setting.LastTestMessage!.Length);
    }

    [TestMethod]
    public void AProviderIdIsRequired()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MetadataProviderSetting("  ", Now));
    }

    [TestMethod]
    public void AnAuditEventRequiresAnActionAndSubjectType()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AuditEvent("", "metadata_provider", null, null, null, null, Now));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new AuditEvent(AuditActions.MetadataProviderEnabled, "", null, null, null, null, Now));
    }
}
