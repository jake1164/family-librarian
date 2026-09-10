using FamilyLibrarian.Application.Publishing;
using FamilyLibrarian.Domain.Publishing;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

[TestClass]
public sealed class ExternalLibraryLinksTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CwaLinkPrefersPublicUrlOverOpdsBaseUrl()
    {
        var settings = EnabledCwaSettings(opdsBaseUrl: "http://cwa:8083", publicUrl: "https://library.example.net");

        var link = ExternalLibraryLinks.BuildCwaBookLink(settings, "42");

        Assert.AreEqual("https://library.example.net/book/42", link?.ToString());
    }

    [TestMethod]
    public void CwaLinkFallsBackToOpdsBaseUrlWhenNoPublicUrl()
    {
        var settings = EnabledCwaSettings(opdsBaseUrl: "https://cwa.example.test", publicUrl: null);

        var link = ExternalLibraryLinks.BuildCwaBookLink(settings, "42");

        Assert.AreEqual("https://cwa.example.test/book/42", link?.ToString());
    }

    [TestMethod]
    public void CwaLinkTrimsATrailingSlashOnTheBaseUrl()
    {
        var settings = EnabledCwaSettings(opdsBaseUrl: "https://cwa.example.test/", publicUrl: null);

        var link = ExternalLibraryLinks.BuildCwaBookLink(settings, "42");

        Assert.AreEqual("https://cwa.example.test/book/42", link?.ToString());
    }

    [TestMethod]
    public void CwaLinkIsNullWhenSettingsAreNull()
    {
        Assert.IsNull(ExternalLibraryLinks.BuildCwaBookLink(null, "42"));
    }

    [TestMethod]
    public void CwaLinkIsNullWhenDisabled()
    {
        var settings = new CwaSettings(Now);
        settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey,
            "https://cwa.example.test", null, null, null, null, Now);
        settings.SetEnabled(false, null, Now);

        Assert.IsNull(ExternalLibraryLinks.BuildCwaBookLink(settings, "42"));
    }

    [TestMethod]
    public void CwaLinkIsNullWhenBookIdIsMissing()
    {
        var settings = EnabledCwaSettings(opdsBaseUrl: "https://cwa.example.test", publicUrl: null);

        Assert.IsNull(ExternalLibraryLinks.BuildCwaBookLink(settings, null));
    }

    [TestMethod]
    public void AudiobookshelfLinkPrefersPublicUrlOverBaseUrl()
    {
        var settings = EnabledAudiobookshelfSettings(baseUrl: "http://abs:80", publicUrl: "https://audio.example.net");

        var link = ExternalLibraryLinks.BuildAudiobookshelfItemLink(settings, "li_123");

        Assert.AreEqual("https://audio.example.net/item/li_123", link?.ToString());
    }

    [TestMethod]
    public void AudiobookshelfLinkFallsBackToBaseUrlWhenNoPublicUrl()
    {
        var settings = EnabledAudiobookshelfSettings(baseUrl: "https://abs.example.test", publicUrl: null);

        var link = ExternalLibraryLinks.BuildAudiobookshelfItemLink(settings, "li_123");

        Assert.AreEqual("https://abs.example.test/item/li_123", link?.ToString());
    }

    [TestMethod]
    public void AudiobookshelfLinkTrimsATrailingSlashOnTheBaseUrl()
    {
        var settings = EnabledAudiobookshelfSettings(baseUrl: "https://abs.example.test/", publicUrl: null);

        var link = ExternalLibraryLinks.BuildAudiobookshelfItemLink(settings, "li_123");

        Assert.AreEqual("https://abs.example.test/item/li_123", link?.ToString());
    }

    [TestMethod]
    public void AudiobookshelfLinkIsNullWhenSettingsAreNull()
    {
        Assert.IsNull(ExternalLibraryLinks.BuildAudiobookshelfItemLink(null, "li_123"));
    }

    [TestMethod]
    public void AudiobookshelfLinkIsNullWhenDisabled()
    {
        var settings = new AudiobookshelfSettings(Now);
        settings.SetSettings("https://abs.example.test", null, null, null, null, Now);
        settings.SetEnabled(false, null, Now);

        Assert.IsNull(ExternalLibraryLinks.BuildAudiobookshelfItemLink(settings, "li_123"));
    }

    [TestMethod]
    public void AudiobookshelfLinkIsNullWhenItemIdIsMissing()
    {
        var settings = EnabledAudiobookshelfSettings(baseUrl: "https://abs.example.test", publicUrl: null);

        Assert.IsNull(ExternalLibraryLinks.BuildAudiobookshelfItemLink(settings, null));
    }

    private static CwaSettings EnabledCwaSettings(string? opdsBaseUrl, string? publicUrl)
    {
        var settings = new CwaSettings(Now);
        settings.SetSettings(
            CwaTransportMode.Local, "/ingest", null, null, null, null, CwaSftpAuthenticationMode.PrivateKey,
            opdsBaseUrl, publicUrl, null, null, null, Now);
        settings.SetEnabled(true, null, Now);
        return settings;
    }

    private static AudiobookshelfSettings EnabledAudiobookshelfSettings(string? baseUrl, string? publicUrl)
    {
        var settings = new AudiobookshelfSettings(Now);
        settings.SetSettings(baseUrl, publicUrl, null, null, null, Now);
        settings.SetEnabled(true, null, Now);
        return settings;
    }
}
