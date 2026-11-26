// One-shot expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Catalog;

/// <summary>
/// The byte budget of a lookup window is the size of its <em>final</em> canonical snapshot: the retained items, the subjects issued
/// with them, the final header (counts, notes, completion) and the truncation flag. These tests measure that snapshot exactly as the
/// store serializes it, at the configured boundary, at the production default and around Unicode text, and they pin the stated
/// contributor role through the same storage. All data is invented; nothing here is a provider response.
/// </summary>
[TestClass]
public sealed class CatalogDiscoveryBudgetTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static ObservedDiscovery Header(string providerName = "Demo Source", string subjectLabel = "Saga") => new(
        Guid.NewGuid(), "GetAuthorSeries", "demo", providerName, "Ok", "ExhaustedInCheckedScope", [DiscoveryCodes.NoNativeSeriesId],
        "Supported", "scripted", Guid.NewGuid(), Guid.NewGuid(), ObservedSubjectKind.Author, subjectLabel, 1, 0, 1, 0, 0, Now)
    {
        ContinuationToken = "0123456789abcdef"
    };

    private static WindowRef Ref(int index, int labelLength, int idLength, char fill = 'x') => new(
        ObservedSubjectKind.Series, new string(fill, labelLength - 3) + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
        new string('i', idLength - 3) + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
        "native", null, null, null, null, null, null);

    private static int SizeOf(CatalogBrowseContextStore store, Guid context) =>
        JsonSerializer.SerializeToUtf8Bytes(store.TryGetSnapshot(Owner, context, Now)!, WebJson).Length;

    private static DiscoveryWindowLease? Admit(CatalogBrowseContextStore store, DiscoveryWindowInput input) =>
        store.AdmitDiscoveryWindow(Owner, input, Now.AddMinutes(10), Now);

    private static DiscoveryWindowInput Refs(int count, int labelLength, int idLength, string providerName = "Demo Source", string subjectLabel = "Saga", char fill = 'x', bool token = true) =>
        new(Header(providerName, subjectLabel) with { ContinuationToken = token ? "0123456789abcdef" : null }, providerName, [], Enumerable.Range(0, count).Select(i => Ref(i, labelLength, idLength, fill)).ToArray());

    private static CatalogBrowseContextStore Store(int bytes) => new(new CatalogBrowseContextLimits { MaxBytesPerContext = bytes });

    // ================================================================ N2: the final snapshot is the bound

    [TestMethod]
    public void ConfiguredBoundariesNeverLetTheFinalSnapshotExceedTheBudgetAtAnyByte()
    {
        var input = Refs(3, 40, 40);
        var roomy = Store(1_000_000);
        var full = SizeOf(roomy, Admit(roomy, input)!.ContextId);
        var previous = -1;
        var sawCut = false;

        for (var limit = 300; limit <= full + 3; limit++)
        {
            var store = Store(limit);
            var lease = Admit(store, input);
            if (lease is null) continue;

            var size = SizeOf(store, lease.ContextId);
            Assert.IsLessThanOrEqualTo(limit, size, $"limit {limit}: the stored snapshot is {size} bytes.");
            // Tight, not merely safe: a budget of exactly what was stored keeps exactly as much.
            Assert.AreEqual(lease.Retained, Admit(Store(size), input)!.Retained, $"limit {limit}: the stored size is itself enough.");
            Assert.IsGreaterThanOrEqualTo(previous, lease.Retained, $"limit {limit}: a larger budget never keeps fewer items.");
            previous = lease.Retained;
            var window = store.TryGetWindow(Owner, lease.ContextId, Now)!;
            Assert.AreEqual(lease.Retained, window.Header.ItemsRetained);
            Assert.AreEqual(lease.Retained, window.Refs.Count);
            if (lease.Retained < lease.Offered)
            {
                sawCut = true;
                Assert.AreEqual("ContextCapacity", window.Header.Completion);
                Assert.IsNull(window.Header.ContinuationToken, "A cut result offers no continuation.");
                CollectionAssert.Contains(window.Header.Notes.ToArray(), "context-capacity");
                Assert.IsTrue(window.Truncated);
                CollectionAssert.AreEqual(
                    input.Refs.Take(lease.Retained).Select(item => item.NativeId).ToArray(),
                    window.Refs.Select(item => item.NativeId).ToArray(), "The kept items are the contiguous prefix, ids whole.");
            }
        }

        Assert.IsTrue(sawCut, "The sweep crossed the boundary.");
        var exact = Store(full);
        Assert.AreEqual(3, Admit(exact, input)!.Retained, "A budget of exactly the full size keeps everything.");
        var oneShort = Store(full - 1);
        Assert.IsLessThan(3, Admit(oneShort, input)!.Retained, "One byte short is a cut, stated as one.");
    }

    [TestMethod]
    public void TheProductionDefaultKeepsTwoHundredMaximumShapedRefsWithinTheFinalByteBound()
    {
        // Native ids of the existing 512-character maximum, 200-character labels, a 19-character provider name and a 147-character
        // subject label: the shape whose final snapshot (cut header and truncation flag included) once reached 262,145 bytes.
        var store = new CatalogBrowseContextStore();
        var input = Refs(200, 200, 512, providerName: new string('p', 19), subjectLabel: new string('s', 147), token: false);

        var lease = Admit(store, input)!;

        var size = SizeOf(store, lease.ContextId);
        Assert.IsLessThanOrEqualTo(256 * 1024, size, "The final canonical snapshot is within 256 KiB.");
        Assert.IsLessThan(200, lease.Retained, "Two hundred such refs cannot all fit.");
        Assert.IsGreaterThan(100, lease.Retained, "A large prefix still does.");
        var window = store.TryGetWindow(Owner, lease.ContextId, Now)!;
        Assert.AreEqual(lease.Retained, window.Refs.Count);
        Assert.AreEqual(200, window.Header.ItemsOffered);
        Assert.IsNull(window.Header.ContinuationToken);
        Assert.AreEqual("ContextCapacity", window.Header.Completion);
        Assert.IsTrue(window.Refs.All(item => item.NativeId!.Length == 512), "No native id was truncated.");

        // Whatever the subject label and provider name lengths, the final snapshot never passes the bound (the overrun was a few
        // bytes and depended on exactly where the cut landed, so every nearby length is tried).
        for (var labelLength = 120; labelLength <= 200; labelLength++)
        {
            var sweepStore = new CatalogBrowseContextStore();
            var sweep = Admit(sweepStore, Refs(200, 200, 512, providerName: new string('p', 19), subjectLabel: new string('s', labelLength), token: false))!;
            Assert.IsLessThanOrEqualTo(256 * 1024, SizeOf(sweepStore, sweep.ContextId), $"subject label length {labelLength}");
        }
    }

    [TestMethod]
    public void BooksAndRefsTogetherAndUnicodeTextAreMeasuredExactlyToo()
    {
        var books = Enumerable.Range(0, 6).Select(i => new WindowBook(
            new SourceRef("demo", "w" + i, ExternalReferenceEntityType.Work), "題名 𝔘𝔫𝔦𝔠𝔬𝔡𝔢 \"quoted\" <b>" + i + "</b>", new DateOnly(1965, 1, 1), DatePrecision.Year, DateBasis.Original,
            [new WindowCredit(0, "著者 Ørsted", "a" + i, "illustrator")],
            [new WindowMembership("シリーズ", "s" + i, "第" + i + "巻", i, "native", null, null)], null, null, null)).ToArray();
        var refs = Enumerable.Range(0, 3).Select(i => Ref(i, 60, 30, '語')).ToArray();
        var input = new DiscoveryWindowInput(Header(subjectLabel: "題名"), "Demo Source", books, refs);
        var roomy = Store(1_000_000);
        var full = SizeOf(roomy, Admit(roomy, input)!.ContextId);

        for (var limit = full - 900; limit <= full + 2; limit += 7)
        {
            var store = Store(limit);
            var lease = Admit(store, input);
            if (lease is null) continue;
            Assert.IsLessThanOrEqualTo(limit, SizeOf(store, lease.ContextId), $"limit {limit}");
            var window = store.TryGetWindow(Owner, lease.ContextId, Now)!;
            Assert.AreEqual(lease.Retained, window.Records.Count + window.Refs.Count);
            if (window.Records.Count < books.Length) Assert.AreEqual(0, window.Refs.Count, "Refs follow every book; a cut never skips ahead.");
        }
    }

    [TestMethod]
    public void AHeaderThatCannotFitIsRefusedBeforeAnythingIsEvictedAndAnEmptyAnswerStillFitsOrIsRefusedTruthfully()
    {
        var store = Store(1_500);
        var seed = store.Observe(Owner, "run:a", [new BrowseObservation("demo", "Demo Source", true, [new BookCandidate(
            "demo", "Demo Source", "b", "B", ["A"], null, null, null, [], [])])], Now)!;
        var contextsBefore = store.Statistics().Contexts;
        var hugeHeader = Header() with { Notes = Enumerable.Repeat("a-stable-note-code", 120).ToArray() };

        var refused = Admit(store, new DiscoveryWindowInput(hugeHeader, "Demo Source", [], []));
        var empty = Admit(store, new DiscoveryWindowInput(Header(), "Demo Source", [], []));

        Assert.IsNull(refused, "Not even the header fits: nothing is admitted.");
        Assert.AreEqual(contextsBefore + 1, store.Statistics().Contexts, "The refusal evicted nothing; only the later, fitting empty answer was admitted.");
        Assert.IsNotNull(store.TryGetSnapshot(Owner, seed.ContextId, Now), "The earlier context survived the refusal.");
        Assert.AreEqual(0, empty!.Retained);
        Assert.AreEqual(0, empty.Offered);
        var window = store.TryGetWindow(Owner, empty.ContextId, Now)!;
        Assert.AreEqual("ExhaustedInCheckedScope", window.Header.Completion, "An answer with no items is not a cut.");
        Assert.IsFalse(window.Truncated);
    }

    [TestMethod]
    public void WhenEveryItemIsDroppedTheHeaderStatesTheCutAndTheCountsTruthfully()
    {
        var input = Refs(4, 60, 30);
        var probe = Store(1_000_000);
        var headerOnly = new DiscoveryWindowInput(input.Header, "Demo Source", [], []);
        var baseline = SizeOf(probe, Admit(probe, headerOnly)!.ContextId);
        var store = Store(baseline + 120);

        var lease = Admit(store, input)!;

        Assert.AreEqual(0, lease.Retained);
        Assert.AreEqual(4, lease.Offered);
        var window = store.TryGetWindow(Owner, lease.ContextId, Now)!;
        Assert.AreEqual(4, window.Header.ItemsOffered);
        Assert.AreEqual(0, window.Header.ItemsRetained);
        Assert.AreEqual("ContextCapacity", window.Header.Completion);
        Assert.IsTrue(window.Truncated);
        Assert.IsEmpty(window.Refs);
        Assert.IsLessThanOrEqualTo(baseline + 120, SizeOf(store, lease.ContextId));
    }

    // ================================================================ N3: stated roles survive storage

    private static WindowBook Book(params WindowCredit[] credits) => new(
        new SourceRef("demo", "w1", ExternalReferenceEntityType.Work), "Work One", null, null, null, credits, [], null, null, null);

    [TestMethod]
    public void AStatedRoleIsKeptExactlyBoundToItsOrdinalAndIdAndAnAbsentRoleStaysAbsent()
    {
        var store = new CatalogBrowseContextStore();
        var input = new DiscoveryWindowInput(Header(), "Demo Source",
        [
            Book(new WindowCredit(0, "Frank Herbert", "A1", null),
                 new WindowCredit(1, "Pat Painter", null, "illustrator"),
                 new WindowCredit(2, "Tess Translator", "A3", " translator "),
                 new WindowCredit(5, "Sam Same", "A4", "author"))
        ], []);

        var window = store.TryGetWindow(Owner, Admit(store, input)!.ContextId, Now)!;

        var credits = window.Records.Single().Credits;
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 5 }, credits.Select(credit => credit.Ordinal).ToArray(), "Source ordinals are kept, gaps and all.");
        CollectionAssert.AreEqual(new string?[] { null, "illustrator", "translator", "author" }, credits.Select(credit => credit.Role).ToArray(),
            "A stated role is kept (trimmed); an absent one is not turned into 'author'.");
        CollectionAssert.AreEqual(new string?[] { "A1", null, "A3", "A4" }, credits.Select(credit => credit.NativeAuthorId).ToArray());
        Assert.AreEqual(4, credits.Select(credit => credit.SubjectId).Distinct().Count(), "Each credit keeps its own subject.");
        Assert.IsFalse(window.Records.Single().FactsTruncated);
    }

    [TestMethod]
    [DataRow(64, false)]
    [DataRow(65, true)]
    [DataRow(200, true)]
    public void AnOversizedRoleIsDroppedWholeNeverCutAndTheCreditSaysARoleWasStatedButNotKept(int length, bool dropped)
    {
        var store = new CatalogBrowseContextStore();
        var role = "illustrator-" + new string('x', length - "illustrator-".Length);
        var input = new DiscoveryWindowInput(Header(), "Demo Source", [Book(new WindowCredit(0, "Pat Painter", "A1", role))], []);

        var record = store.TryGetWindow(Owner, Admit(store, input)!.ContextId, Now)!.Records.Single();
        var credit = record.Credits.Single();

        Assert.AreEqual("Pat Painter", credit.Label);
        Assert.AreEqual("A1", credit.NativeAuthorId, "The id and ordinal binding are untouched.");
        if (dropped)
        {
            Assert.IsNull(credit.Role, "A cut role could change meaning, so it is dropped whole.");
            Assert.IsTrue(credit.RoleNotRetained, "Lost is not absent: the credit remembers a role was stated.");
            Assert.IsTrue(record.FactsTruncated, "The record-level flag is kept too.");
        }
        else
        {
            Assert.AreEqual(role, credit.Role);
            Assert.IsFalse(credit.RoleNotRetained);
            Assert.IsFalse(record.FactsTruncated);
        }
    }

    [TestMethod]
    public void OnlyTheOversizedCreditIsMarkedAndAbsentAndAuthorRolesAreNeverMarked()
    {
        var store = new CatalogBrowseContextStore();
        var input = new DiscoveryWindowInput(Header(), "Demo Source",
        [
            Book(new WindowCredit(0, "Ann Author", "A1", null),
                 new WindowCredit(1, "Pat Painter", null, "illustrator-" + new string('x', 70)),
                 new WindowCredit(2, "Bo Writer", "A3", "author"),
                 new WindowCredit(3, "Tess Translator", null, "translator"))
        ], []);

        var credits = store.TryGetWindow(Owner, Admit(store, input)!.ContextId, Now)!.Records.Single().Credits;

        CollectionAssert.AreEqual(new[] { false, true, false, false }, credits.Select(credit => credit.RoleNotRetained).ToArray());
        CollectionAssert.AreEqual(new string?[] { null, null, "author", "translator" }, credits.Select(credit => credit.Role).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, credits.Select(credit => credit.Ordinal).ToArray());
    }

    [TestMethod]
    public void TheNotRetainedMarkIsCountedInTheBudgetAndOtherCreditsSerializeAsBefore()
    {
        var plain = new ObservedCredit(0, "X", Guid.Empty);
        var marked = plain with { RoleNotRetained = true };
        string Json(ObservedCredit credit) => System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(credit, WebJson));

        Assert.DoesNotContain("roleNotRetained", Json(plain), "A credit that lost nothing serializes exactly as before.");
        StringAssert.Contains(Json(marked), "\"roleNotRetained\":true");
        var store = new CatalogBrowseContextStore();
        var lost = new DiscoveryWindowInput(Header(), "Demo Source", [Book(new WindowCredit(0, "Pat Painter", null, new string('r', 65)))], []);
        var none = new DiscoveryWindowInput(Header(), "Demo Source", [Book(new WindowCredit(0, "Pat Painter", null, null))], []);
        var a = SizeOf(store, Admit(store, lost)!.ContextId);
        var b = SizeOf(store, Admit(store, none)!.ContextId);
        Assert.IsGreaterThanOrEqualTo("\"roleNotRetained\":true".Length, a - b, "The mark (and the flag it implies) are real serialized bytes in the final snapshot.");
    }

    [TestMethod]
    public void RolesAreCountedInTheSerializedBudgetAndOldRecordsWithoutOneStillSerializeIdentically()
    {
        var withRole = new DiscoveryWindowInput(Header(), "Demo Source", [Book(new WindowCredit(0, "Pat Painter", null, "illustrator"))], []);
        var without = new DiscoveryWindowInput(Header(), "Demo Source", [Book(new WindowCredit(0, "Pat Painter", null, null))], []);
        var store = new CatalogBrowseContextStore();
        var a = SizeOf(store, Admit(store, withRole)!.ContextId);
        var b = SizeOf(store, Admit(store, without)!.ContextId);

        Assert.AreEqual("\"role\":\"illustrator\"".Length + 1, a - b, "The role is a real serialized field, charged to the budget.");
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new ObservedCredit(0, "X", Guid.Empty), WebJson));
        Assert.DoesNotContain("role", json, "A credit with no role serializes exactly as it did before roles existed.");
    }
}
