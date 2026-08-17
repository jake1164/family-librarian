using FamilyLibrarian.Web.Client.Catalog;

namespace FamilyLibrarian.Web.Tests;

[TestClass]
public sealed class CatalogDescriptionMarkupTests
{
    [TestMethod]
    public void ToMarkupRendersTheAllowedFormattingTags()
    {
        var markup = CatalogDescriptionMarkup.ToMarkup(
            "<p>One <strong>important</strong> paragraph.</p><blockquote>A quotation.</blockquote>");

        Assert.AreEqual(
            "<p>One <strong>important</strong> paragraph.</p><blockquote>A quotation.</blockquote>",
            markup.Value);
    }

    [TestMethod]
    public void ToMarkupEncodesUnsupportedTagsAndAllAttributes()
    {
        var markup = CatalogDescriptionMarkup.ToMarkup(
            "<p onclick=\"stealCookies()\">Unsafe paragraph.</p><img src=x onerror=stealCookies()>");

        Assert.AreEqual(
            "&lt;p onclick=&quot;stealCookies()&quot;&gt;Unsafe paragraph.&lt;/p&gt;&lt;img src=x onerror=stealCookies()&gt;",
            markup.Value);
    }
}
