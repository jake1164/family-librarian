using FamilyLibrarian.Application.Providers;

namespace FamilyLibrarian.Infrastructure.Tests.Providers;

[TestClass]
public sealed class ProviderCatalogEntryParserTests
{
    private static readonly string[] ExpectedCapabilities = ["search", "acquire"];


    [TestMethod]
    public void AWellFormedEntriesArrayParsesEveryField()
    {
        const string json = """
            [
                {
                    "id": "sample-provider",
                    "name": "Sample Provider",
                    "protocolVersion": "1.0",
                    "capabilities": ["search", "acquire"],
                    "license": "MIT",
                    "publisher": "Family Librarian",
                    "trustLabel": "official",
                    "ociImageDigest": "sha256:abc123",
                    "homepageUrl": "https://example.test",
                    "description": "A reference provider."
                }
            ]
            """;

        var entries = ProviderCatalogEntryParser.Parse(json);

        Assert.AreEqual(1, entries.Count);
        var entry = entries[0];
        Assert.AreEqual("sample-provider", entry.Id);
        Assert.AreEqual("Sample Provider", entry.Name);
        Assert.AreEqual("1.0", entry.ProtocolVersion);
        CollectionAssert.AreEqual(ExpectedCapabilities, entry.Capabilities.ToArray());
        Assert.AreEqual("MIT", entry.License);
        Assert.AreEqual("Family Librarian", entry.Publisher);
        Assert.AreEqual("official", entry.TrustLabel);
        Assert.AreEqual("sha256:abc123", entry.OciImageDigest);
        Assert.AreEqual("https://example.test", entry.HomepageUrl);
        Assert.AreEqual("A reference provider.", entry.Description);
    }

    [TestMethod]
    public void AnEntryMissingIdIsSkippedRatherThanThrowing()
    {
        const string json = """
            [
                { "name": "No id here" },
                { "id": "has-id", "name": "Has Id" }
            ]
            """;

        var entries = ProviderCatalogEntryParser.Parse(json);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("has-id", entries[0].Id);
    }

    [TestMethod]
    public void AnEntryMissingNameFallsBackToItsId()
    {
        const string json = """[{ "id": "no-name" }]""";

        var entries = ProviderCatalogEntryParser.Parse(json);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("no-name", entries[0].Name);
    }

    [TestMethod]
    public void MalformedJsonReturnsAnEmptyListRatherThanThrowing()
    {
        var entries = ProviderCatalogEntryParser.Parse("not json at all {{{");

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void NonArrayJsonReturnsAnEmptyList()
    {
        var entries = ProviderCatalogEntryParser.Parse("""{ "id": "not-an-array" }""");

        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public void NullOrWhitespaceInputReturnsAnEmptyList()
    {
        Assert.AreEqual(0, ProviderCatalogEntryParser.Parse(null).Count);
        Assert.AreEqual(0, ProviderCatalogEntryParser.Parse("   ").Count);
        Assert.AreEqual(0, ProviderCatalogEntryParser.Parse(string.Empty).Count);
    }
}
