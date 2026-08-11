using FamilyLibrarian.Infrastructure.Metadata;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

[TestClass]
public sealed class QueryRedactingHttpClientLoggerTests
{
    [TestMethod]
    public void TheApiKeyQueryIsStrippedFromAnAbsoluteUri()
    {
        var uri = new Uri("https://www.googleapis.com/books/v1/volumes?q=dune&key=super-secret-key");

        var redacted = QueryRedactingHttpClientLogger.Redact(uri);

        Assert.AreEqual("https://www.googleapis.com/books/v1/volumes", redacted);
        StringAssert.DoesNotMatch(redacted, new System.Text.RegularExpressions.Regex("super-secret-key"));
    }

    [TestMethod]
    public void TheQueryIsStrippedFromARelativeUri()
    {
        var uri = new Uri("volumes?q=dune&key=super-secret-key", UriKind.Relative);

        var redacted = QueryRedactingHttpClientLogger.Redact(uri);

        Assert.AreEqual("volumes", redacted);
    }

    [TestMethod]
    public void AUriWithNoQueryIsUnchanged()
    {
        var uri = new Uri("https://www.googleapis.com/books/v1/volumes/abc123");

        Assert.AreEqual(
            "https://www.googleapis.com/books/v1/volumes/abc123",
            QueryRedactingHttpClientLogger.Redact(uri));
    }

    [TestMethod]
    public void ANullUriDoesNotThrow()
    {
        Assert.AreEqual("(no uri)", QueryRedactingHttpClientLogger.Redact(null));
    }
}
