using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Domain.Tests.Publishing;

[TestClass]
public sealed class CwaSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NewSettingsDefaultToPrivateKeyAuthentication()
    {
        var settings = new CwaSettings(Now);

        Assert.AreEqual(CwaSftpAuthenticationMode.PrivateKey, settings.SftpAuthenticationMode);
    }

    [TestMethod]
    public void ChangingTheSftpEndpointClearsTrustedHostKey()
    {
        var settings = new CwaSettings(Now);
        settings.SetSettings(
            CwaTransportMode.Sftp,
            null,
            "sftp.example.test",
            22,
            "cwa",
            "/ingest",
            CwaSftpAuthenticationMode.Password,
            null,
            null,
            null,
            Now);
        settings.TrustSftpHostKey("SHA256:initial-fingerprint", null, Now);

        settings.SetSettings(
            CwaTransportMode.Sftp,
            null,
            "replacement.example.test",
            22,
            "cwa",
            "/ingest",
            CwaSftpAuthenticationMode.Password,
            null,
            null,
            null,
            Now.AddMinutes(1));

        Assert.IsNull(settings.SftpHostKeyFingerprint);
        Assert.IsNull(settings.SftpHostKeyTrustedAtUtc);
    }

    [TestMethod]
    public void ChangingAuthenticationDoesNotClearTrustedHostKey()
    {
        var settings = new CwaSettings(Now);
        settings.SetSettings(
            CwaTransportMode.Sftp,
            null,
            "sftp.example.test",
            22,
            "cwa",
            "/ingest",
            CwaSftpAuthenticationMode.PrivateKey,
            null,
            null,
            null,
            Now);
        settings.TrustSftpHostKey("SHA256:initial-fingerprint", null, Now);

        settings.SetSettings(
            CwaTransportMode.Sftp,
            null,
            "sftp.example.test",
            22,
            "cwa",
            "/ingest",
            CwaSftpAuthenticationMode.Password,
            null,
            null,
            null,
            Now.AddMinutes(1));

        Assert.AreEqual("SHA256:initial-fingerprint", settings.SftpHostKeyFingerprint);
    }
}
