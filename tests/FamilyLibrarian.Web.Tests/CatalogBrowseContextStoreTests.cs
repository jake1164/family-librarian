// One-shot expected-value arrays in assertions are clearer inline than as static fields; CA1861 targets repeated calls.
#pragma warning disable CA1861
using System.Globalization;
using System.Text.Json;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Web.Catalog;

namespace FamilyLibrarian.Web.Tests;

/// <summary>
/// The temporary browse cache is a bounded, owner-private operational store. Time is always passed in, so these tests
/// pin the clock instead of sleeping.
/// </summary>
[TestClass]
public sealed class CatalogBrowseContextStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>Measurement is independent of the store: the test serializes the retained snapshot itself.</summary>
    private static readonly JsonSerializerOptions IndependentJson = new(JsonSerializerDefaults.Web);

    private static byte[] Serialize(CatalogBrowseContextStore store, Guid owner, Guid contextId, DateTimeOffset now) =>
        JsonSerializer.SerializeToUtf8Bytes(store.TryGetSnapshot(owner, contextId, now)!, IndependentJson);

    internal static BookCandidate Book(
        string external,
        string[]? authors = null,
        BookSeriesCandidate[]? series = null,
        string provider = "example-source",
        string? title = null) =>
        new(provider, "Example Source", external, title ?? $"Title {external}", authors ?? ["Ada Example"], null, null, null,
            [], series ?? []);

    private static BrowseObservation Observation(
        string provider = "example-source", bool succeeded = true, params BookCandidate[] candidates) =>
        new(provider, "Example Source", succeeded, candidates);

    private static BrowseContextLease Observe(
        CatalogBrowseContextStore store, Guid owner, string scope, DateTimeOffset now, params BookCandidate[] candidates) =>
        store.Observe(owner, scope, [Observation(candidates: candidates)], now)!;

    [TestMethod]
    public void RepeatedObservationOfOneScopeReusesOneContextAndAddsNothing()
    {
        var store = new CatalogBrowseContextStore();
        var first = Observe(store, Alice, "run:1", T0, Book("a", series: [new("Saga", "1", true)]));
        var before = store.Statistics();

        for (var poll = 0; poll < 50; poll++)
            Assert.AreEqual(first.ContextId, Observe(store, Alice, "run:1", T0.AddSeconds(poll), Book("a", series: [new("Saga", "1", true)])).ContextId);

        Assert.AreEqual(before, store.Statistics(), "Polling must not grow contexts, indexes or subjects.");
        Assert.AreEqual(first.ExpiresAtUtc, Observe(store, Alice, "run:1", T0.AddMinutes(5), Book("a")).ExpiresAtUtc, "Expiry is not renewed by use.");
    }

    [TestMethod]
    public void AppendingKeepsEveryIssuedSubjectAndRecordUnchanged()
    {
        var store = new CatalogBrowseContextStore();
        var lease = Observe(store, Alice, "run:1", T0, Book("a", series: [new("Saga", "1", true)]));
        var original = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record;

        // Later results add a record and re-report "a" with different labels; the stored observation is immutable.
        Observe(store, Alice, "run:1", T0.AddSeconds(30),
            Book("a", authors: ["Someone Else"], series: [new("Other", "9", true)]),
            Book("b", series: [new("Saga", "2", true)]));

        var again = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0.AddSeconds(31))!.Record;
        Assert.AreSame(original, again);
        var subject = original.Credits.Single().SubjectId;
        var sample = store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0.AddSeconds(31))!;
        Assert.AreEqual("Ada Example", sample.Subject.Label);
        Assert.AreEqual("a", sample.Subject.AnchorExternalId, "The subject stays bound to the record that first issued it.");
        Assert.HasCount(2, sample.ProviderRecords, "Appended records join the source sample.");
        var saga = original.Memberships.Single().SubjectId;
        var laterSaga = store.TryGetRecord(Alice, lease.ContextId, "example-source", "b", T0.AddSeconds(31))!.Record.Memberships.Single().SubjectId;
        Assert.AreNotEqual(
            saga, laterSaga,
            "An equal label in another record is a different source observation, so it has its own subject.");
        Assert.AreEqual("a", store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Series, saga, T0.AddSeconds(31))!.Subject.AnchorExternalId);
        Assert.AreEqual("b", store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Series, laterSaga, T0.AddSeconds(31))!.Subject.AnchorExternalId);
    }

    [TestMethod]
    public void SubjectsAreScopedToTheirProviderAndLabelKind()
    {
        var store = new CatalogBrowseContextStore();
        var lease = store.Observe(Alice, "run:1",
        [
            Observation("source-one", candidates: Book("a", ["Same Name"], [new("Same Name", "1", true)], "source-one")),
            Observation("source-two", candidates: Book("b", ["Same Name"], [], "source-two"))
        ], T0)!;

        var one = store.TryGetRecord(Alice, lease.ContextId, "source-one", "a", T0)!.Record;
        var two = store.TryGetRecord(Alice, lease.ContextId, "source-two", "b", T0)!.Record;
        Assert.AreNotEqual(one.Credits.Single().SubjectId, two.Credits.Single().SubjectId, "A label seen in two sources is two subjects.");
        Assert.AreNotEqual(one.Credits.Single().SubjectId, one.Memberships.Single().SubjectId, "An author and a series with one name are two subjects.");

        var sample = store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, one.Credits.Single().SubjectId, T0)!;
        Assert.IsTrue(sample.ProviderRecords.All(record => record.ProviderId == "source-one"), "A sample never includes another source's records.");
        Assert.IsNull(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Series, one.Credits.Single().SubjectId, T0), "Wrong kind.");
    }

    [TestMethod]
    public void ExpiryIsAbsoluteAndEnforcedOnReadWithoutAnyInsert()
    {
        var store = new CatalogBrowseContextStore();
        var lease = Observe(store, Alice, "run:1", T0, Book("a"));
        var subject = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record.Credits.Single().SubjectId;

        // Reading just before expiry must not extend it.
        Assert.IsNotNull(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0.AddMinutes(9).AddSeconds(59)));
        Assert.IsNull(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0.AddMinutes(10)));
        Assert.IsNull(store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0.AddMinutes(10)));
        Assert.AreEqual(0, store.Statistics().Contexts, "A read of an expired context removes it.");
    }

    [TestMethod]
    public void InsertsPruneExpiredContextsAndEveryIndexShrinks()
    {
        var store = new CatalogBrowseContextStore();
        for (var index = 0; index < 5; index++) Observe(store, Alice, $"run:{index}", T0, Book("a"));
        Assert.AreEqual(5, store.Statistics().Contexts);

        Observe(store, Bob, "run:x", T0.AddMinutes(11), Book("a"));

        Assert.AreEqual(new BrowseStoreStatistics(1, 1, 1, 1), store.Statistics());
        Assert.AreEqual(0, store.ContextCountFor(Alice));
    }

    [TestMethod]
    public void AnotherOwnersContextIsIndistinguishableFromAnUnknownOne()
    {
        var store = new CatalogBrowseContextStore();
        var lease = Observe(store, Alice, "run:1", T0, Book("a"));
        var subject = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record.Credits.Single().SubjectId;

        Assert.IsNull(store.TryGetSample(Bob, lease.ContextId, ObservedSubjectKind.Author, subject, T0));
        Assert.IsNull(store.TryGetSample(Alice, Guid.NewGuid(), ObservedSubjectKind.Author, subject, T0));
        Assert.IsNull(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, Guid.NewGuid(), T0));
        Assert.IsNull(store.TryGetRecord(Bob, lease.ContextId, "example-source", "a", T0));
        Assert.IsNotNull(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0), "The owner is unaffected.");
        Assert.AreNotEqual(
            lease.ContextId,
            Observe(store, Bob, "run:1", T0, Book("a")).ContextId,
            "The same scope key for another owner is a different context.");
    }

    [TestMethod]
    public void OwnerLimitEvictsTheOldestContextOfThatOwnerOnly()
    {
        var store = new CatalogBrowseContextStore();
        var bobLease = Observe(store, Bob, "run:b", T0, Book("a"));
        var alice = Enumerable.Range(0, 10).Select(index => Observe(store, Alice, $"run:{index}", T0, Book("a"))).ToArray();
        Assert.AreEqual(10, store.ContextCountFor(Alice));

        var newest = Observe(store, Alice, "run:new", T0, Book("a"));

        Assert.AreEqual(10, store.ContextCountFor(Alice));
        Assert.IsNull(store.TryGetRecord(Alice, alice[0].ContextId, "example-source", "a", T0), "The oldest was evicted.");
        Assert.IsNotNull(store.TryGetRecord(Alice, alice[1].ContextId, "example-source", "a", T0));
        Assert.IsNotNull(store.TryGetRecord(Alice, newest.ContextId, "example-source", "a", T0));
        Assert.IsNotNull(store.TryGetRecord(Bob, bobLease.ContextId, "example-source", "a", T0), "Another owner is untouched.");
        var stats = store.Statistics();
        Assert.AreEqual(stats.Contexts, stats.OwnerIndexEntries);
        Assert.AreEqual(stats.Contexts, stats.ScopeIndexEntries);
    }

    [TestMethod]
    public void TotalLimitEvictsTheOldestContextAndIndexesStayInStep()
    {
        var store = new CatalogBrowseContextStore();
        var leases = new List<BrowseContextLease>();
        for (var owner = 0; owner < 101; owner++)
            leases.Add(Observe(store, Guid.NewGuid(), "run:1", T0, Book("a")));

        var stats = store.Statistics();
        Assert.AreEqual(100, stats.Contexts);
        Assert.AreEqual(100, stats.OwnerIndexEntries);
        Assert.AreEqual(100, stats.ScopeIndexEntries);
        Assert.AreEqual(100, stats.SubjectIndexEntries, "One author subject per context; evicted subjects are gone.");
    }

    [TestMethod]
    public void RecordCreditAndMembershipCapsAreEnforcedAndDisclosed()
    {
        var store = new CatalogBrowseContextStore();
        var tooMany = Enumerable.Range(0, 25).Select(index => $"Author {index}").ToArray();
        var memberships = Enumerable.Range(0, 25).Select(index => new BookSeriesCandidate($"Series {index}", index.ToString(CultureInfo.InvariantCulture), false)).ToArray();
        var lease = Observe(store, Alice, "run:1", T0, Book("wide", tooMany, memberships));

        var record = store.TryGetRecord(Alice, lease.ContextId, "example-source", "wide", T0)!.Record;
        Assert.HasCount(20, record.Credits);
        Assert.HasCount(20, record.Memberships);
        Assert.IsTrue(record.CreditsTruncated);
        Assert.IsTrue(record.MembershipsTruncated);

        var records = Enumerable.Range(0, 250).Select(index => Book($"b{index}")).ToArray();
        var second = Observe(store, Bob, "run:1", T0, records);
        var lookup = store.TryGetRecord(Bob, second.ContextId, "example-source", "b199", T0)!;
        Assert.IsTrue(lookup.ContextTruncated);
        Assert.IsNotNull(lookup.Record);
        Assert.IsNull(store.TryGetRecord(Bob, second.ContextId, "example-source", "b200", T0), "Record 201 is not retained.");
    }

    [TestMethod]
    public void OversizedLabelsAreOmittedNotTruncatedIntoLookalikes()
    {
        var store = new CatalogBrowseContextStore();
        var longName = new string('x', 201);
        var lease = Observe(store, Alice, "run:1", T0,
            Book("a", [longName, "Real Author"], [new(longName, "1", true), new("Real Series", new string('9', 65), false)]));

        var record = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record;
        Assert.AreEqual("Real Author", record.Credits.Single().Label);
        Assert.AreEqual(1, record.Credits.Single().Ordinal, "The surviving credit keeps its original position.");
        Assert.AreEqual("Real Series", record.Memberships.Single().Label);
        Assert.IsNull(record.Memberships.Single().PositionLabel, "An oversized position text is dropped, not cut.");
        Assert.IsTrue(record.CreditsTruncated);
        Assert.IsTrue(record.MembershipsTruncated);
    }

    [TestMethod]
    public void OversizedIdentifiersAreNeverRetained()
    {
        var store = new CatalogBrowseContextStore();
        var longExternal = new string('e', 513);
        var lease = Observe(store, Alice, "run:1", T0, Book(longExternal), Book("ok"));

        Assert.IsNull(store.TryGetRecord(Alice, lease.ContextId, "example-source", longExternal, T0));
        Assert.IsTrue(store.TryGetRecord(Alice, lease.ContextId, "example-source", "ok", T0)!.ContextTruncated);

        var longProvider = new string('p', 129);
        var second = store.Observe(
            Alice, "run:2", [Observation(longProvider, candidates: Book("a", provider: longProvider))], T0)!;
        Assert.IsNull(store.TryGetRecord(Alice, second.ContextId, longProvider, "a", T0));
    }

    [TestMethod]
    public void TheByteBudgetStopsRetentionAndDisclosesIt()
    {
        var store = new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxBytesPerContext = 4_000 });
        var heavy = Enumerable.Range(0, 40).Select(index =>
            Book($"book-{index}", [new string((char)('a' + index % 26), 150) + index], [new(new string('s', 150) + index, "1", true)])).ToArray();

        var lease = Observe(store, Alice, "run:1", T0, heavy);

        var kept = heavy.Count(candidate => store.TryGetRecord(Alice, lease.ContextId, "example-source", candidate.ExternalId, T0) is not null);
        Assert.IsGreaterThan(0, kept);
        Assert.IsLessThan(heavy.Length, kept, "The budget bounds how much one context retains.");
        Assert.IsTrue(store.TryGetRecord(Alice, lease.ContextId, "example-source", "book-0", T0)!.ContextTruncated);
        Assert.IsLessThanOrEqualTo(4_000, Serialize(store, Alice, lease.ContextId, T0).Length,
            "The size limit stops retention before the serialized context exceeds its budget.");
    }

    [TestMethod]
    public void TheSerializedContextStaysInsideTheBudgetWithNonAsciiControlQuoteAndBackslashText()
    {
        // Codex's review probe shape: 200 unique records with 300-character CJK titles, plus escaping-sensitive
        // characters. Default System.Text.Json escaping makes these bytes three to six times their UTF-8 length.
        var hostile = string.Concat(Enumerable.Repeat("\u4E66", 147)) + "\u0001\"\\" + string.Concat(Enumerable.Repeat("\u4E66", 150));
        Assert.AreEqual(300, hostile.Length);
        var store = new CatalogBrowseContextStore();
        var records = Enumerable.Range(0, 200)
            .Select(index => Book($"book-{index}", ["Shared"], [new("Saga", "1", true)], title: hostile))
            .ToArray();

        var lease = Observe(store, Alice, "run:1", T0, records);

        var snapshot = store.TryGetSnapshot(Alice, lease.ContextId, T0)!;
        var serialized = JsonSerializer.SerializeToUtf8Bytes(snapshot, IndependentJson);
        Assert.IsLessThanOrEqualTo(CatalogBrowseContextLimits.Default.MaxBytesPerContext, serialized.Length,
            "The retained context, serialized with default escaping, fits the promised 256 KiB.");
        Assert.IsTrue(snapshot.Truncated, "Admission stops inside the budget and says so.");
        Assert.IsLessThan(200, snapshot.Records.Count, "Not every record fits once escaping is counted.");
        Assert.AreEqual(hostile, snapshot.Records[0].Title, "A retained title keeps its exact text.");
    }

    [TestMethod]
    public void AdmissionIsNearTheSerializedBoundaryAndARejectedRecordLeavesNothingBehind()
    {
        var reference = new CatalogBrowseContextStore();
        var referenceLease = Observe(reference, Alice, "run:ref", T0,
            Book("a", ["Ada Example"], [new("Saga", "1", true)]));
        var singleBytes = Serialize(reference, Alice, referenceLease.ContextId, T0).Length;

        // The budget is the independently measured size of one record plus a small margin: one fits, the rest cannot.
        var store = new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxBytesPerContext = singleBytes + 16 });
        var lease = Observe(store, Alice, "run:1", T0,
            Book("a", ["Ada Example"], [new("Saga", "1", true)]),
            Book("b", ["Grace Other"], [new("Other Saga", "2", true)]));

        var snapshot = store.TryGetSnapshot(Alice, lease.ContextId, T0)!;
        Assert.HasCount(1, snapshot.Records);
        Assert.AreEqual("a", snapshot.Records[0].ExternalId);
        Assert.AreEqual("a", snapshot.Subjects.Select(subject => subject.AnchorExternalId).Distinct().Single(),
            "Only the retained record issued subjects.");
        Assert.IsTrue(snapshot.Truncated);
        Assert.IsLessThanOrEqualTo(singleBytes + 16, JsonSerializer.SerializeToUtf8Bytes(snapshot, IndependentJson).Length);

        var statistics = store.Statistics();
        Assert.AreEqual(snapshot.Subjects.Count, statistics.SubjectIndexEntries,
            "A rejected record leaves no orphan subject index entry.");
        Assert.AreEqual(snapshot.Subjects.Count, snapshot.Subjects.Select(subject => subject.SubjectId).Distinct().Count());
        Assert.IsFalse(snapshot.Subjects.Any(subject => subject.Label is "Grace Other" or "Other Saga"),
            "Nothing was issued for the rejected record.");
    }

    [TestMethod]
    public void ProviderFailureMetadataIsChargedAgainstTheSameSerializedBudget()
    {
        var store = new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxBytesPerContext = 1_024 });
        var failures = Enumerable.Range(0, 60)
            .Select(index => Observation(new string((char)('a' + index % 26), 100) + index, succeeded: false))
            .ToArray();

        var lease = store.Observe(Alice, "run:1", failures, T0)!;

        var snapshot = store.TryGetSnapshot(Alice, lease.ContextId, T0)!;
        Assert.IsTrue(snapshot.Truncated);
        Assert.IsLessThan(60, snapshot.FailedProviders.Count, "Failure metadata is bounded by the same budget.");
        Assert.IsGreaterThan(0, snapshot.FailedProviders.Count);
        Assert.IsLessThanOrEqualTo(1_024, JsonSerializer.SerializeToUtf8Bytes(snapshot, IndependentJson).Length);
    }

    [TestMethod]
    public void EqualLabelsAtDifferentRecordsOrOrdinalsAreDistinctSubjectsBoundToTheirOwnAnchor()
    {
        var store = new CatalogBrowseContextStore();
        var lease = Observe(store, Alice, "run:1", T0,
            Book("a", ["Shared"]),
            Book("b", ["Other", "Shared"]),
            Book("c", ["Shared", "Shared"]));

        var a = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record;
        var b = store.TryGetRecord(Alice, lease.ContextId, "example-source", "b", T0)!.Record;
        var c = store.TryGetRecord(Alice, lease.ContextId, "example-source", "c", T0)!.Record;

        var aShared = a.Credits.Single();
        var bShared = b.Credits.Single(credit => credit.Ordinal == 1);
        Assert.AreNotEqual(aShared.SubjectId, bShared.SubjectId, "The same label in two records is two source observations.");
        var bSample = store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, bShared.SubjectId, T0)!;
        Assert.AreEqual("b", bSample.Subject.AnchorExternalId, "The subject is bound to the record whose ordinal issued it.");
        Assert.AreEqual(1, bSample.Subject.Ordinal);

        Assert.HasCount(2, c.Credits);
        Assert.AreNotEqual(c.Credits[0].SubjectId, c.Credits[1].SubjectId, "Two ordinals in one record are two observations.");
        var second = store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, c.Credits[1].SubjectId, T0)!;
        Assert.AreEqual("c", second.Subject.AnchorExternalId);
        Assert.AreEqual(1, second.Subject.Ordinal);
        Assert.AreEqual(
            c.Credits[0].SubjectId,
            store.TryGetRecord(Alice, lease.ContextId, "example-source", "c", T0.AddSeconds(1))!.Record.Credits[0].SubjectId,
            "Repeated observation keeps every subject id stable.");
    }

    [TestMethod]
    public void KnownFormatsAndVersionAreRetainedAndOversizedFactsAreDropped()
    {
        var store = new CatalogBrowseContextStore();
        var candidate = Book("a", ["Ada Example"]) with
        {
            Editions =
            [
                new BookEditionCandidate("Alpha", null, "Ebook", null),
                new BookEditionCandidate("Alpha audio", null, "Audiobook", null),
                new BookEditionCandidate("Alpha ebook again", null, "Ebook", null),
                new BookEditionCandidate("Odd", null, new string('f', 65), null)
            ],
            AssessedVersion = new BookCandidateVersion("Collection", "Collection or combined volume", "Includes other writings.")
        };
        var unknown = Book("b", ["Ada Example"]);

        var lease = Observe(store, Alice, "run:1", T0, candidate, unknown);

        var known = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record;
        CollectionAssert.AreEqual(new[] { "Ebook", "Audiobook" }, known.Formats.ToArray(), "Distinct formats, in source order.");
        Assert.AreEqual("Collection", known.VersionKind);
        Assert.AreEqual("Collection or combined volume", known.VersionLabel);
        Assert.IsTrue(known.FactsTruncated, "An oversized format value is dropped and disclosed.");

        var blank = store.TryGetRecord(Alice, lease.ContextId, "example-source", "b", T0)!.Record;
        Assert.IsEmpty(blank.Formats);
        Assert.AreEqual("Unspecified", blank.VersionKind, "The existing assessment still says the version is unknown.");
        Assert.IsFalse(blank.FactsTruncated);
    }

    [TestMethod]
    public void ACacheThatCannotAdmitReturnsNullInsteadOfThrowing()
    {
        var store = new CatalogBrowseContextStore(new CatalogBrowseContextLimits { MaxContextsTotal = 0 });
        Assert.IsNull(store.Observe(Alice, "run:1", [Observation(candidates: Book("a"))], T0));
        Assert.AreEqual(0, store.Statistics().Contexts);
    }

    [TestMethod]
    public void AProviderFailureIsRememberedAndClearedByALaterSuccess()
    {
        var store = new CatalogBrowseContextStore();
        var lease = store.Observe(Alice, "run:1", [Observation(candidates: Book("a"))], T0)!;
        var subject = store.TryGetRecord(Alice, lease.ContextId, "example-source", "a", T0)!.Record.Credits.Single().SubjectId;
        Assert.IsFalse(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0)!.ProviderFailed);

        store.Observe(Alice, "run:1", [Observation(succeeded: false)], T0);
        Assert.IsTrue(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0)!.ProviderFailed);

        store.Observe(Alice, "run:1", [Observation(candidates: Book("a"))], T0);
        Assert.IsFalse(store.TryGetSample(Alice, lease.ContextId, ObservedSubjectKind.Author, subject, T0)!.ProviderFailed);
    }

    [TestMethod]
    public async Task ConcurrentObservationsAndReadsKeepEveryBoundAndIndexConsistent()
    {
        var store = new CatalogBrowseContextStore();
        var owners = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
        {
            try
            {
                var random = new Random(worker);
                for (var step = 0; step < 400; step++)
                {
                    var owner = owners[random.Next(owners.Length)];
                    var scope = $"run:{random.Next(14)}";
                    var now = T0.AddSeconds(random.Next(0, 900));
                    var lease = store.Observe(owner, scope, [Observation(candidates: Book($"b{random.Next(30)}", [$"Author {random.Next(6)}"]))], now);
                    if (lease is null) continue;
                    var record = store.TryGetRecord(owner, lease.ContextId, "example-source", "x", now);
                    Assert.IsNull(record);
                    store.Statistics();
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.IsEmpty(failures, string.Join(" | ", failures.Select(failure => failure.Message)));
        var stats = store.Statistics();
        Assert.IsLessThanOrEqualTo(80, stats.Contexts, "Eight owners at ten contexts each.");
        Assert.AreEqual(stats.Contexts, stats.OwnerIndexEntries);
        Assert.AreEqual(stats.Contexts, stats.ScopeIndexEntries);
        foreach (var owner in owners) Assert.IsLessThanOrEqualTo(10, store.ContextCountFor(owner));
    }
}
