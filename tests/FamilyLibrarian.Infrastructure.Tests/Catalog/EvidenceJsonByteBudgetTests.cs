using System.Text;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Identity;
using FamilyLibrarian.Application.Catalog.Observation;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The evidence writer enforces the accepted budgets on the serialized UTF-8 bytes it actually stores: 16 KiB per claim value and
/// 64 KiB per snapshot. Multibyte text, escapes and surrogate pairs all count at their serialized size, truncation never cuts a
/// character in half, the JSON stays valid, and a shortened value says so.
/// </summary>
[TestClass]
public sealed class EvidenceJsonByteBudgetTests
{
    private const int ClaimBudget = 16 * 1024;
    private const int SnapshotBudget = 64 * 1024;

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    private static string TextOf(string json, string property) => JsonDocument.Parse(json).RootElement.GetProperty(property).GetString()!;

    private static bool Truncated(string json) =>
        JsonDocument.Parse(json).RootElement.TryGetProperty("truncated", out var flag) && flag.GetBoolean();

    [TestMethod]
    [DataRow("a", 20_000, "ASCII")]
    [DataRow("界", 12_000, "three-byte characters")]
    [DataRow("\"", 10_000, "quotes escaped to two bytes each")]
    [DataRow("\u0001", 5_000, "control characters escaped to six bytes each")]
    [DataRow("\U0001F4DA", 6_000, "surrogate pairs")]
    public void ATextClaimNeverExceedsSixteenKibibytesWhateverItsEncodingAndStaysValidJson(string unit, int repeat, string why)
    {
        var original = string.Concat(Enumerable.Repeat(unit, repeat));

        var json = EvidenceJson.TextValue("text", original);

        Assert.IsLessThanOrEqualTo(ClaimBudget, Bytes(json), why);
        Assert.IsLessThanOrEqualTo(ClaimBudget, EvidenceJson.StoredBytes(json), "Also as PostgreSQL's jsonb returns it: " + why);
        Assert.IsTrue(Truncated(json), "A value that was shortened says so: " + why);
        var kept = TextOf(json, "text");
        Assert.IsTrue(original.StartsWith(kept, StringComparison.Ordinal), "What remains is a prefix of the original: " + why);
        Assert.IsGreaterThan(ClaimBudget / 2, Bytes(json), "It keeps as much as the budget allows rather than discarding most of it: " + why);
        for (var index = 0; index < kept.Length; index++)
        {
            if (char.IsHighSurrogate(kept[index]))
            {
                Assert.IsTrue(index + 1 < kept.Length && char.IsLowSurrogate(kept[index + 1]), "No pair was cut in half: " + why);
                index++;
            }
            else
            {
                Assert.IsFalse(char.IsLowSurrogate(kept[index]), "No lone low surrogate: " + why);
            }
        }
    }

    [TestMethod]
    public void ATextClaimThatFitsIsStoredWholeAndUnflagged()
    {
        // jsonb returns one extra space after the colon, so the largest value that is stored whole is one byte shorter.
        var fits = new string('a', ClaimBudget - EvidenceJson.StoredBytes("{\"text\":\"\"}"));
        var tooLong = fits + "a";

        var whole = EvidenceJson.TextValue("text", fits);
        var shortened = EvidenceJson.TextValue("text", tooLong);

        Assert.AreEqual(ClaimBudget, EvidenceJson.StoredBytes(whole));
        Assert.IsFalse(Truncated(whole));
        Assert.AreEqual(fits, TextOf(whole, "text"));
        Assert.IsLessThanOrEqualTo(ClaimBudget, Bytes(shortened));
        Assert.IsTrue(Truncated(shortened));
    }

    private static BookCandidate Candidate(int authors, int authorChars, int editions, int titleChars) =>
        new("demo", "Demo", new string('界', 300), new string('界', titleChars),
            Enumerable.Range(0, authors).Select(index => $"{index:D3}" + new string('界', authorChars)).ToArray(),
            "Description.", new string('界', 500), null,
            Enumerable.Range(0, editions)
                .Select(index => new BookEditionCandidate(
                    new string('界', titleChars) + index, $"97800000{index:D5}"[..13], new string('界', 60), null, "en", new string('界', 500)))
                .ToArray(),
            Enumerable.Range(0, 30).Select(index => new BookSeriesCandidate(new string('界', 400) + index, "1", index == 0)).ToArray(),
            Subjects: Enumerable.Range(0, 40).Select(index => new string('界', 200) + index).ToArray(),
            Language: "en");

    [TestMethod]
    public void ASnapshotOfMultibyteTextNeverExceedsSixtyFourKibibytesAndSaysWhenItWasShortened()
    {
        var candidate = Candidate(authors: 60, authorChars: 600, editions: 100, titleChars: 600);
        var projection = CatalogEditionProjection.Project(candidate, null);

        var (json, hash) = EvidenceJson.Snapshot(candidate, projection, "Observed", null, CatalogVersionClass.Text, ["Bound"]);

        Assert.IsLessThanOrEqualTo(SnapshotBudget, Bytes(json), "A character count of this text would have been under 64 K while the bytes were far over.");
        Assert.IsLessThanOrEqualTo(SnapshotBudget, EvidenceJson.StoredBytes(json), "Also as PostgreSQL's jsonb returns it.");
        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("truncated").GetBoolean(), "The snapshot discloses that it was reduced.");
        Assert.AreEqual("Observed", document.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(EvidenceJson.Hash(json), hash, "The hash is of exactly the stored text.");
        var (again, _) = EvidenceJson.Snapshot(candidate, projection, "Observed", null, CatalogVersionClass.Text, ["Bound"]);
        Assert.AreEqual(json, again, "The reduction is deterministic, so a repeat is recognised.");
        var domain = new SourceObservation(Guid.NewGuid(), "demo", "r1", hash, json, EvidenceJson.SchemaVersion, DateTimeOffset.UtcNow);
        Assert.IsNotNull(domain, "The domain accepts what the writer produced.");
    }

    [TestMethod]
    public void AnOrdinaryBookSnapshotIsNeitherReducedNorFlagged()
    {
        var candidate = Candidate(authors: 1, authorChars: 5, editions: 2, titleChars: 10) with
        {
            Series = [], Subjects = ["Fantasy"], CoverUrl = null, Publisher = null
        };
        var projection = CatalogEditionProjection.Project(candidate, null);

        var (json, _) = EvidenceJson.Snapshot(candidate, projection, "Observed", null, CatalogVersionClass.Text, []);

        Assert.IsFalse(JsonDocument.Parse(json).RootElement.TryGetProperty("truncated", out _));
    }

    [TestMethod]
    public void TheStoredFormAddsOneSpaceAfterEveryStructuralColonAndCommaButNotInsideStrings()
    {
        const string Json = "{\"a\":\"x:y,z\",\"b\":[1,2],\"c\":\"q\\\"r,s\"}";

        Assert.AreEqual(Bytes(Json) + 6, EvidenceJson.StoredBytes(Json), "Three colons and three commas outside strings.");
    }

    [TestMethod]
    public void AnEmbeddedNulIsRemovedBecauseJsonbCannotStoreOne()
    {
        var json = EvidenceJson.TextValue("text", "before\0after");

        Assert.AreEqual("beforeafter", TextOf(json, "text"));
    }

    [TestMethod]
    public void TheRenderedSizeOfASnapshotIsItsEscapedFormNotItsStoredBytes()
    {
        var stored = "{\"filler\":\"" + new string('界', 2_000) + "\"}";

        var rendered = EvidenceJson.RenderedByteCount(stored);

        Assert.IsLessThan(8 * 1024, Bytes(stored), "Stored, it is under 8 KiB.");
        Assert.IsGreaterThan(8 * 1024, rendered, "The default JSON encoder writes each of these characters as a six-byte escape.");
    }
}
