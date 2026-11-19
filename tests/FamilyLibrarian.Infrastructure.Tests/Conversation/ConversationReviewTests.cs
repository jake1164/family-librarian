using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// Q6 re-review: a number selects the identity that was shown (not a position in the retained order), "more" shows what has not been
/// seen whatever order a new page sorts into, an unopenable row never blocks the rows after it, and a request refused after its key
/// was claimed leaves the key and the recorded reply in agreement.
/// </summary>
[TestClass]
public sealed class ConversationReviewTests
{
    private static SourceRef SeriesId(string id) => new("fake", id, ExternalReferenceEntityType.Series);

    private static CatalogAnswerSeries Series(string label, string? id) =>
        new(label, id is null ? null : SeriesId(id), null, RelationshipEvidence.Native, null, null, null);

    private static CatalogAnswer SeriesAnswer(params CatalogAnswerSeries[] series) =>
        new()
        {
            Operation = CatalogAnswerOperation.AuthorSeries,
            Outcome = CatalogAnswerOutcome.Qualified,
            Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, "Mara Quill", TestAnswer.Author),
            Series = series,
            Qualifiers = [CatalogAnswerCodes.ListNotExhaustive],
            Today = new DateOnly(2026, 11, 18)
        };

    /// <summary>An author question answered with the series, and any later question with a one-book series answer.</summary>
    private static ConversationHarness AuthorSeries(CatalogAnswer series)
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Subject switch
        {
            null => TestAnswer.Offering(TestAnswer.AuthorChoice("Mara Quill")),
            { Kind: CatalogSubjectKind.Author } => series,
            _ => TestAnswer.Members(TestAnswer.Entry("The Lantern Road", 1))
        };
        return harness;
    }

    // ---- selection is by the identity that was shown ----------------------------------------------------------------

    [TestMethod]
    public async Task ChoosingTheFirstShownSeriesOpensThatSeriesEvenWhenTheRetainedOrderIsDifferent()
    {
        var shown = SeriesAnswer(Series("Alpha", "A"), Series("Zeta", "Z"));
        var harness = AuthorSeries(shown);
        // The real service sorts what it shows and retains what the source returned: the two orders differ.
        harness.Answers.RetainedSeries = answer => [.. answer.Series.Reverse()];
        await harness.SayAsync("which series has Mara Quill written");
        var listed = await harness.SayAsync("1");
        StringAssert.Contains(listed.Reply.Text, "1. Alpha");

        await harness.SayAsync("1");

        Assert.AreEqual(SeriesId("A"), harness.Answers.Requests[^1].Subject!.Native, "The number 1 on the screen was Alpha.");
    }

    [TestMethod]
    public async Task ChoosingAnOfferedAuthorOrSeriesOpensTheOneShownWhenTheRetainedChoicesAreInAnotherOrder()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Subject is null
            ? TestAnswer.Offering(TestAnswer.AuthorChoice("Mara Quill"), TestAnswer.SeriesChoice("The Lantern Road"))
            : TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        harness.Answers.RetainedChoices = answer => [.. answer.Choices.Reverse()];
        await harness.SayAsync("the latest Lantern Road book");

        await harness.SayAsync("2");

        Assert.AreEqual(CatalogSubjectKind.Series, harness.Answers.Requests[^1].Subject!.Kind, "Choice 2 on the screen was the series.");
        Assert.AreEqual(TestAnswer.Series, harness.Answers.Requests[^1].Subject!.Native);
    }

    [TestMethod]
    public async Task ANumberWhoseShownIdentityIsNotInTheRetainedWindowSelectsNothingAndAsksNothing()
    {
        var harness = AuthorSeries(SeriesAnswer(Series("Alpha", "A"), Series("Zeta", "Z")));
        harness.Answers.RetainedSeries = _ => [Series("Someone else's", "Q")];
        await harness.SayAsync("which series has Mara Quill written");
        await harness.SayAsync("1");
        var before = harness.Answers.Requests.Count;

        var reply = await harness.SayAsync("1");

        Assert.AreEqual(before, harness.Answers.Requests.Count, "A reference the retained window does not hold is never asked about.");
        StringAssert.Contains(reply.Reply.Text, "expired");
    }

    // ---- a row that cannot be opened never blocks the rows after it --------------------------------------------------

    [TestMethod]
    public async Task ASeriesWhoseIdIsTooLongToKeepIsListedAsInformationAndTheRowsAfterItAreNumbered()
    {
        var longId = new string('x', 121);
        var harness = AuthorSeries(SeriesAnswer(Series("Long Id Saga", longId), Series("Alpha", "A"), Series("Beta", "B")));
        await harness.SayAsync("which series has Mara Quill written");

        var reply = await harness.SayAsync("1");

        StringAssert.Contains(reply.Reply.Text, "Long Id Saga");
        StringAssert.Contains(reply.Reply.Text, "can't be opened here");
        StringAssert.Contains(reply.Reply.Text, "1. Alpha");
        StringAssert.Contains(reply.Reply.Text, "2. Beta");
        Assert.HasCount(2, reply.Reply.Choices);

        await harness.SayAsync("1");
        Assert.AreEqual(SeriesId("A"), harness.Answers.Requests[^1].Subject!.Native);
    }

    [TestMethod]
    public async Task MoreAdvancesPastAnUnopenableSeriesToTheRowsThatWereNotShown()
    {
        var series = new[] { Series("Long Id Saga", new string('x', 121)) }
            .Concat(Enumerable.Range(1, 9).Select(index => Series("Series " + index, "S" + index)))
            .ToArray();
        var harness = AuthorSeries(SeriesAnswer(series));
        await harness.SayAsync("which series has Mara Quill written");
        var first = await harness.SayAsync("1");
        var shownFirst = first.Reply.Choices.Count;

        var more = await harness.SayAsync("more");

        Assert.IsGreaterThan(0, shownFirst);
        StringAssert.Contains(more.Reply.Text, "Series 7", "The rows after the first page are shown, not the empty page again.");
        Assert.IsFalse(more.Reply.Text.Contains("Long Id Saga", StringComparison.Ordinal), "The informational row is not repeated.");
        Assert.IsGreaterThan(0, more.Reply.Choices.Count);
    }

    // ---- "more" shows what has not been seen -------------------------------------------------------------------------

    [TestMethod]
    public async Task MoreShowsTheUnseenRecordsWhenANewPageSortsInAmongTheEarlierOnes()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.ContinuationToken is null
            ? TestAnswer.Members(TestAnswer.Entry("Birch", 2), TestAnswer.Entry("Dogwood", 4)) with { ContinuationToken = "page-2" }
            : TestAnswer.Members(
                TestAnswer.Entry("Alder", 1), TestAnswer.Entry("Birch", 2), TestAnswer.Entry("Cedar", 3), TestAnswer.Entry("Dogwood", 4));
        var first = await harness.SayAsync("reading order of Lantern Road");
        StringAssert.Contains(first.Reply.Text, "Birch");
        StringAssert.Contains(first.Reply.Text, "Dogwood");

        var more = await harness.SayAsync("more");

        StringAssert.Contains(more.Reply.Text, "1. Alder", "A new record that sorted in before the ones shown is not skipped.");
        StringAssert.Contains(more.Reply.Text, "2. Cedar");
        Assert.IsFalse(more.Reply.Text.Contains("Birch", StringComparison.Ordinal), "A record already shown is not shown again.");
        Assert.IsFalse(more.Reply.Text.Contains("Dogwood", StringComparison.Ordinal));
        Assert.HasCount(2, more.Reply.Choices);

        var nothing = await harness.SayAsync("more");
        StringAssert.Contains(nothing.Reply.Text, "nothing more");
    }

    [TestMethod]
    public async Task MoreShowsTheRecordsTheCapHidAndReadsNothingFromTheSource()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(Enumerable.Range(1, 8).Select(index => TestAnswer.Entry("Book " + index, index)).ToArray());
        var first = await harness.SayAsync("reading order of Lantern Road");
        Assert.HasCount(5, first.Reply.Choices);

        var more = await harness.SayAsync("more");

        Assert.IsNull(harness.Answers.Requests[^1].ContinuationToken, "These were already read: no provider page is spent.");
        StringAssert.Contains(more.Reply.Text, "1. Book 6");
        StringAssert.Contains(more.Reply.Text, "3. Book 8");
        Assert.HasCount(3, more.Reply.Choices);
        Assert.IsFalse(more.Reply.Text.Contains("Book 5", StringComparison.Ordinal));
    }

    // ---- a refusal after the key claim leaves the key and the reply in agreement ---------------------------------------

    [TestMethod]
    public async Task AWithdrawalDuringTheKeyClaimReleasesTheKeyRecordsTheRefusalAndCreatesNothing()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        await harness.SayAsync("the latest Lantern Road book");
        harness.Events.OnClaimKey = () => harness.Accounts.Status = UserStatus.Invited;

        var reply = await harness.SayAsync("request 1 audiobook", eventId: "$withdrawn");

        Assert.AreEqual(ConversationReplyKind.Unauthenticated, reply.Reply.Kind);
        Assert.IsEmpty(harness.Tools.Created, "The account was withdrawn after the claim, so nothing is created.");
        Assert.IsEmpty(harness.Events.Keys, "The claimed key was given back, so it does not read as an earlier request.");

        harness.Accounts.Status = UserStatus.Active;
        var replay = await harness.SayAsync("request 1 audiobook", eventId: "$withdrawn");
        Assert.IsTrue(replay.Replayed, "The same message is answered from its recorded refusal.");
        Assert.AreEqual(ConversationReplyKind.Unauthenticated, replay.Reply.Kind);
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task AnAnswerThatExpiresDuringTheKeyClaimReleasesTheKeyAndCreatesNothing()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        await harness.SayAsync("the latest Lantern Road book");
        harness.Events.OnClaimKey = () => harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(11);

        var reply = await harness.SayAsync("request 1 audiobook", eventId: "$expired");

        StringAssert.Contains(reply.Reply.Text, "expired");
        Assert.IsEmpty(harness.Tools.Created);
        Assert.IsEmpty(harness.Events.Keys);
    }

    [TestMethod]
    public async Task ARequestThatIsMadeKeepsItsKeyWithTheRequestAttached()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        await harness.SayAsync("the latest Lantern Road book");

        var reply = await harness.SayAsync("request 1 audiobook", eventId: "$made");

        Assert.AreEqual(ConversationReplyKind.RequestCreated, reply.Reply.Kind);
        Assert.AreEqual(reply.Reply.RequestId, harness.Events.Keys.Single().Value.RequestId);
    }
}
