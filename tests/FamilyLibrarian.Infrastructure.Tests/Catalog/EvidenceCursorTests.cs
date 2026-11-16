using System.Text;
using FamilyLibrarian.Application.Catalog.Observation;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// A continuation the host issues must be one the host accepts, for every text key the catalog model can store: the encoder, the decoder,
/// the endpoint's size limit and the documented bound are one contract. Keys are never truncated; malformed, foreign-version,
/// cross-category and wrongly shaped tokens are still refused.
/// </summary>
[TestClass]
public sealed class EvidenceCursorTests
{
    private static string Latin(int length) => new('b', length);

    private static string Cjk(int length) => new('界', length);

    // A supplementary-plane character is two UTF-16 units and four UTF-8 bytes: two per unit, so no worse than the BMP's three.
    private static string Astral(int length) => string.Concat(Enumerable.Repeat("𠀀", length / 2));

    public static IEnumerable<object[]> LegalDuplicateKeys =>
    [
        [Latin(350), Latin(64)],
        [Latin(600), Latin(64)],
        [Latin(1_000), Latin(512)],
        [Cjk(1_000), Cjk(512)],
        [Astral(1_000), Astral(512)],
        ["", ""],
        ["a title", ""]
    ];

    [TestMethod]
    [DynamicData(nameof(LegalDuplicateKeys))]
    public void EveryDuplicateKeyThatEncodeIssuesDecodesToTheSameKey(string title, string author)
    {
        var token = EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, title, author);

        CollectionAssert.AreEqual(new[] { title, author }, EvidenceCursor.Decode(EvidenceIssueCategory.PossibleDuplicates, token));
        Assert.IsTrue(EvidenceCursor.IsValid(EvidenceIssueCategory.PossibleDuplicates, token));
        Assert.IsLessThanOrEqualTo(EvidenceInspectionLimits.MaximumCursorLength, token.Length);
    }

    [TestMethod]
    public void TheObservedFailureLengthsNowRoundTrip()
    {
        // 350 + 64 was a 583-character token the old decoder refused; a 600-character title could not be encoded at all.
        var token = EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, Latin(350), Latin(64));
        Assert.IsGreaterThan(512, token.Length);
        Assert.IsNotNull(EvidenceCursor.Decode(EvidenceIssueCategory.PossibleDuplicates, token));
        Assert.IsNotNull(EvidenceCursor.Decode(EvidenceIssueCategory.PossibleDuplicates, EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, Latin(600), "x")));
    }

    [TestMethod]
    public void TheWorstCaseTokenFitsTheDocumentedLimitAndAKestrelRequestLine()
    {
        var worst = EvidenceCursor.Encode(
            EvidenceIssueCategory.PossibleDuplicates, Cjk(EvidenceCursor.MaximumTitleKeyLength), Cjk(EvidenceCursor.MaximumAuthorKeyLength));

        Assert.IsLessThanOrEqualTo(EvidenceInspectionLimits.MaximumCursorLength, worst.Length);
        Assert.AreEqual(EvidenceInspectionLimits.MaximumCursorLength, worst.Length, 100, "The documented limit is the real worst case plus a small header allowance, not a guess.");
        // Kestrel's default request line is 8,192 bytes: path, query name and a token this size fit with room to spare.
        Assert.IsLessThan(8_192 - 256, worst.Length + "/api/v1/admin/catalog/issues?category=possible-duplicates&take=100&cursor=".Length);
    }

    [TestMethod]
    [DataRow(EvidenceIssueCategory.SameNameAuthors, 512)]
    [DataRow(EvidenceIssueCategory.SameNameSeries, 1_000)]
    public void EverySingleTextCategoryRoundTripsItsLongestLegalKey(EvidenceIssueCategory category, int maximum)
    {
        foreach (var key in new[] { Latin(maximum), Cjk(maximum), Astral(maximum), Latin(1) })
        {
            var token = EvidenceCursor.Encode(category, key);

            CollectionAssert.AreEqual(new[] { key }, EvidenceCursor.Decode(category, token));
        }
    }

    [TestMethod]
    public void EveryIdCategoryRoundTripsItsIds()
    {
        var one = Guid.NewGuid().ToString("D");
        var two = Guid.NewGuid().ToString("D");

        CollectionAssert.AreEqual(new[] { one, two }, EvidenceCursor.Decode(EvidenceIssueCategory.ProvisionalCredits, EvidenceCursor.Encode(EvidenceIssueCategory.ProvisionalCredits, one, two)));
        foreach (var category in new[]
                 {
                     EvidenceIssueCategory.UnboundSeriesClaims, EvidenceIssueCategory.ConflictedPositions, EvidenceIssueCategory.ContestedObservations
                 })
        {
            CollectionAssert.AreEqual(new[] { one }, EvidenceCursor.Decode(category, EvidenceCursor.Encode(category, one)));
        }
    }

    [TestMethod]
    public void AKeyThatIsNotAdmittedByTheCategoryIsRefusedNeverTruncated()
    {
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.SameNameAuthors, Latin(513)));
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.SameNameSeries, Latin(1_001)));
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, Latin(1_001), "a"));
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, "a", Latin(513)));
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, "only one part"));
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.UnboundSeriesClaims, "not-a-guid"));
        Assert.Throws<ArgumentException>(() => EvidenceCursor.Encode(EvidenceIssueCategory.UnboundSeriesClaims, Guid.Empty.ToString("D")));
    }

    [TestMethod]
    public void AKeyContainingTheOldSeparatorOrControlCharactersStillRoundTrips()
    {
        var odd = "a\u001fb\u0000c\n";

        CollectionAssert.AreEqual(new[] { odd, "x\u001f" }, EvidenceCursor.Decode(
            EvidenceIssueCategory.PossibleDuplicates, EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, odd, "x\u001f")));
    }

    [TestMethod]
    public void NoCursorIsTheFirstPageAndAnythingElseMustBeAKey()
    {
        Assert.IsNull(EvidenceCursor.Decode(EvidenceIssueCategory.PossibleDuplicates, null));
        Assert.IsNull(EvidenceCursor.Decode(EvidenceIssueCategory.PossibleDuplicates, string.Empty));

        var good = EvidenceCursor.Encode(EvidenceIssueCategory.SameNameAuthors, "twin");
        var bytes = Convert.FromBase64String(good.Replace('-', '+').Replace('_', '/').PadRight((good.Length + 3) / 4 * 4, '='));
        string Token(byte[] raw) => Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var malformed = new[]
        {
            "%%%", "YWJj", "AAAA", "djF8bm9uZQ", new string('A', 600), new string('A', EvidenceInspectionLimits.MaximumCursorLength + 1),
            Token(bytes[..^1]),                                  // a length prefix running past the end
            Token([.. bytes, 0x00]),                              // trailing bytes
            Token([.. bytes, .. Encoding.UTF8.GetBytes("extra")]),
            Token([0x02, (byte)'v', (byte)'2', .. bytes[3..]]),   // an unknown version
            Token([0x05, 0xC3, 0x28, 0xA0, 0xA1, 0xFF]),          // invalid UTF-8
            Token([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F])           // an absurd length prefix
        };
        foreach (var cursor in malformed)
        {
            Assert.IsFalse(EvidenceCursor.IsValid(EvidenceIssueCategory.SameNameAuthors, cursor), cursor);
            Assert.Throws<InvalidEvidenceCursorException>(() => EvidenceCursor.Decode(EvidenceIssueCategory.SameNameAuthors, cursor));
        }
    }

    [TestMethod]
    public void ATokenOfOneCategoryIsNeverAKeyOfAnother()
    {
        var id = Guid.NewGuid().ToString("D");
        var tokens = new Dictionary<EvidenceIssueCategory, string>
        {
            [EvidenceIssueCategory.PossibleDuplicates] = EvidenceCursor.Encode(EvidenceIssueCategory.PossibleDuplicates, "t", "a"),
            [EvidenceIssueCategory.SameNameAuthors] = EvidenceCursor.Encode(EvidenceIssueCategory.SameNameAuthors, id),
            [EvidenceIssueCategory.SameNameSeries] = EvidenceCursor.Encode(EvidenceIssueCategory.SameNameSeries, id),
            [EvidenceIssueCategory.ProvisionalCredits] = EvidenceCursor.Encode(EvidenceIssueCategory.ProvisionalCredits, id, id),
            [EvidenceIssueCategory.UnboundSeriesClaims] = EvidenceCursor.Encode(EvidenceIssueCategory.UnboundSeriesClaims, id),
            [EvidenceIssueCategory.ConflictedPositions] = EvidenceCursor.Encode(EvidenceIssueCategory.ConflictedPositions, id),
            [EvidenceIssueCategory.ContestedObservations] = EvidenceCursor.Encode(EvidenceIssueCategory.ContestedObservations, id)
        };

        foreach (var (owner, token) in tokens)
        {
            foreach (var category in tokens.Keys.Where(category => category != owner))
            {
                Assert.IsFalse(EvidenceCursor.IsValid(category, token), $"{owner} token as {category}");
            }
        }
    }
}
