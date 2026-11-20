using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Catalog.Discovery;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Accounts;
using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// Q6 corrections (B-R1 to B-R4). Each test is a probe Codex reproduced against the P4-B batch: a withdrawn account or an expired
/// answer reaching the request tools, an author-series answer that omitted its series, a long title that threw, a displayed number
/// the session did not keep, a paged answer with no way to continue, and a replay that lost the actions it had offered.
/// </summary>
[TestClass]
public sealed class ConversationCorrectionTests
{
    private const string Room = "!room:example.test";

    private static ConversationHarness Latest(string title = "Road's End")
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry(title, 4));
        return harness;
    }

    private static bool ReachedRequestTools(ConversationHarness harness) =>
        harness.Tools.Calls.Any(call => call.StartsWith("resolve:", StringComparison.Ordinal) || call == "create") || harness.Tools.Created.Count > 0;

    // ---- B-R1: the account is checked where the work is done --------------------------------------------------------

    [TestMethod]
    public async Task AnAccountWithdrawnAfterTheListWasOfferedReachesNoRequestTool()
    {
        var harness = Latest();
        await harness.SayAsync("the latest Lantern Road book");

        harness.Accounts.Status = UserStatus.Invited;
        var reply = await harness.SayAsync("request 1 audiobook");

        Assert.AreEqual(ConversationReplyKind.Unauthenticated, reply.Reply.Kind);
        Assert.IsFalse(ReachedRequestTools(harness), string.Join(",", harness.Tools.Calls));
    }

    [TestMethod]
    public async Task AnAccountWithdrawnWhileTheWorkIsBeingResolvedCreatesNoRequest()
    {
        var harness = Latest();
        await harness.SayAsync("the latest Lantern Road book");
        harness.Tools.OnResolve = () => harness.Accounts.Status = UserStatus.Invited;

        var reply = await harness.SayAsync("request 1 audiobook");

        Assert.IsEmpty(harness.Tools.Created, "The account was withdrawn before the write; nothing is created.");
        Assert.AreEqual(ConversationReplyKind.Unauthenticated, reply.Reply.Kind);
    }

    [TestMethod]
    public async Task AnAccountWithdrawnBetweenThePickAndTheFormatCreatesNoRequest()
    {
        var harness = Latest();
        await harness.SayAsync("the latest Lantern Road book");
        var asked = await harness.SayAsync("1");
        Assert.AreEqual(ConversationReplyKind.AskFormat, asked.Reply.Kind);

        harness.Accounts.Status = UserStatus.Invited;
        var reply = await harness.SayAsync("audiobook");

        Assert.AreEqual(ConversationReplyKind.Unauthenticated, reply.Reply.Kind);
        Assert.IsFalse(ReachedRequestTools(harness));
    }

    // ---- B-R2: an offered answer keeps its identity and lifetime through every step ---------------------------------

    [TestMethod]
    public async Task AnExpiredAnswerReachesNoRequestToolWhetherRequestedDirectlyOrThroughTheFormatStep()
    {
        var direct = Latest();
        await direct.SayAsync("the latest Lantern Road book");
        direct.Clock.UtcNow = direct.Clock.UtcNow.AddMinutes(11);
        var refused = await direct.SayAsync("request 1 audiobook");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, refused.Reply.Kind);
        StringAssert.Contains(refused.Reply.Text, "expired");
        Assert.IsFalse(ReachedRequestTools(direct), "An expired answer is never turned into a request.");

        var stepped = Latest();
        await stepped.SayAsync("the latest Lantern Road book");
        await stepped.SayAsync("1");
        stepped.Clock.UtcNow = stepped.Clock.UtcNow.AddMinutes(11);
        var late = await stepped.SayAsync("ebook");

        StringAssert.Contains(late.Reply.Text, "expired");
        Assert.IsFalse(ReachedRequestTools(stepped), "The format step keeps the answer's lifetime: it does not outlive it.");
    }

    [TestMethod]
    public async Task AnAnswerThatExpiresWhileTheWorkIsBeingResolvedCreatesNoRequest()
    {
        var harness = Latest();
        await harness.SayAsync("the latest Lantern Road book");
        harness.Tools.OnResolve = () => harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(11);

        var reply = await harness.SayAsync("request 1 audiobook");

        Assert.IsEmpty(harness.Tools.Created, "The answer expired before the write, so nothing is created.");
        StringAssert.Contains(reply.Reply.Text, "expired");
    }

    [TestMethod]
    public async Task DetailsAndAvailabilityOnAnExpiredAnswerMakeNoLookup()
    {
        var harness = Latest();
        await harness.SayAsync("the latest Lantern Road book");
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(11);

        var details = await harness.SayAsync("details 1");
        var available = await harness.SayAsync("is it available");

        StringAssert.Contains(details.Reply.Text, "expired");
        StringAssert.Contains(available.Reply.Text, "expired");
        Assert.IsFalse(harness.Tools.Calls.Any(call => call.StartsWith("details:", StringComparison.Ordinal) || call.StartsWith("library", StringComparison.Ordinal)));
    }

    // ---- B-R3: the series an author answer received are shown, selectable, and a paged answer continues -------------------

    private static CatalogAnswer AuthorSeriesAnswer() =>
        new()
        {
            Operation = CatalogAnswerOperation.AuthorSeries,
            Outcome = CatalogAnswerOutcome.Qualified,
            Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Author, "Mara Quill", TestAnswer.Author),
            Series =
            [
                new CatalogAnswerSeries("The Lantern Road", TestAnswer.Series, null, RelationshipEvidence.Native, null, 4, null),
                new CatalogAnswerSeries(
                    "Quill's Almanac", new SourceRef("fake", "S-ALMANAC", ExternalReferenceEntityType.Series), null,
                    RelationshipEvidence.Derived("author-works-first-page"), null, null, null),
                new CatalogAnswerSeries("An unbound series", null, null, RelationshipEvidence.Derived("author-works-first-page"), null, null, null)
            ],
            Qualifiers = [CatalogAnswerCodes.DerivedEdges, CatalogAnswerCodes.ListNotExhaustive],
            Today = new DateOnly(2026, 11, 18)
        };

    [TestMethod]
    public async Task AnAuthorSeriesAnswerShowsEverySeriesItReceivedAndSaysWhichWereWorkedOut()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Subject is null
            ? TestAnswer.Offering(TestAnswer.AuthorChoice("Mara Quill"))
            : AuthorSeriesAnswer();
        await harness.SayAsync("which series has Mara Quill written");

        var reply = await harness.SayAsync("1");

        StringAssert.Contains(reply.Reply.Text, "The Lantern Road");
        StringAssert.Contains(reply.Reply.Text, "Quill's Almanac");
        StringAssert.Contains(reply.Reply.Text, "An unbound series");
        StringAssert.Contains(reply.Reply.Text, "worked out from the author's works");
        Assert.IsNotNull(reply.Reply.Answers);
        Assert.IsGreaterThanOrEqualTo(3, reply.Reply.Answers![0].Lines.Count, "The card carries the same series as the text.");
        Assert.HasCount(2, reply.Reply.Choices, "Only a series with an identity can be selected.");
    }

    [TestMethod]
    public async Task ASelectedSeriesIsAskedForItsBooksInTheSameRoot()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Subject switch
        {
            null => TestAnswer.Offering(TestAnswer.AuthorChoice("Mara Quill")),
            { Kind: CatalogSubjectKind.Author } => AuthorSeriesAnswer(),
            _ => TestAnswer.Members(TestAnswer.Entry("The Lantern Road", 1))
        };
        await harness.SayAsync("which series has Mara Quill written");
        var series = await harness.SayAsync("1");
        Assert.IsTrue(CatalogAnswerContextRef.TryParse(harness.Sessions.Sessions.Values.Single().State.AnswerContext, out var held));
        var rootOfSeries = held!.RootId;

        var books = await harness.SayAsync("1");

        var request = harness.Answers.Requests[^1];
        Assert.AreEqual(CatalogAnswerOperation.SeriesMembers, request.Operation);
        Assert.AreEqual(CatalogSubjectKind.Series, request.Subject!.Kind);
        Assert.AreEqual(TestAnswer.Series, request.Subject.Native);
        Assert.AreEqual(rootOfSeries, request.ContextRootId, "The follow-up spends the root the answer was retained under.");
        Assert.AreEqual(ConversationReplyKind.Answer, books.Reply.Kind);
        StringAssert.Contains(series.Reply.Text, "Reply with a number");
    }

    private static ConversationHarness Paged()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.ContinuationToken is null
            ? TestAnswer.Members(TestAnswer.Entry("Alder", 1), TestAnswer.Entry("Birch", 2)) with { ContinuationToken = "page-2" }
            : TestAnswer.Members(
                TestAnswer.Entry("Alder", 1), TestAnswer.Entry("Birch", 2), TestAnswer.Entry("Cedar", 3), TestAnswer.Entry("Dogwood", 4));
        return harness;
    }

    [TestMethod]
    public async Task MoreReadsOneMorePageOfThePagedAnswerAndShowsTheNewBooks()
    {
        var harness = Paged();
        await harness.SayAsync("reading order of Lantern Road");
        var before = harness.Answers.Requests.Count;

        var more = await harness.SayAsync("more");

        Assert.AreEqual(before + 1, harness.Answers.Requests.Count, "One explicit continuation is one more read.");
        var request = harness.Answers.Requests[^1];
        Assert.AreEqual("page-2", request.ContinuationToken);
        Assert.IsNotNull(request.ContextRootId);
        Assert.AreEqual(ConversationReplyKind.Answer, more.Reply.Kind);
        StringAssert.Contains(more.Reply.Text, "Cedar");
        StringAssert.Contains(more.Reply.Text, "Dogwood");
        Assert.IsFalse(more.Reply.Text.Contains("1. Alder", StringComparison.Ordinal), "Books already shown are not shown again.");
    }

    [TestMethod]
    public async Task MoreWithNothingLeftToReadSaysSoAndMakesNoCall()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(TestAnswer.Entry("Alder", 1));
        await harness.SayAsync("reading order of Lantern Road");
        var before = harness.Answers.Requests.Count;

        var more = await harness.SayAsync("more");

        Assert.AreEqual(before, harness.Answers.Requests.Count, "An exhausted list is never re-read.");
        StringAssert.Contains(more.Reply.Text, "nothing more");

        var fresh = new ConversationHarness();
        var none = await fresh.SayAsync("more");
        Assert.IsEmpty(fresh.Answers.Requests);
        Assert.AreEqual(ConversationReplyKind.NotUnderstood, none.Reply.Kind);
    }

    [TestMethod]
    public async Task MoreOnAnExpiredOrAnotherReadersListSpendsNothing()
    {
        var expired = Paged();
        await expired.SayAsync("reading order of Lantern Road");
        var before = expired.Answers.Requests.Count;
        expired.Clock.UtcNow = expired.Clock.UtcNow.AddMinutes(11);
        var late = await expired.SayAsync("more");

        StringAssert.Contains(late.Reply.Text, "expired");
        Assert.AreEqual(before, expired.Answers.Requests.Count);

        var foreign = Paged();
        await foreign.SayAsync("reading order of Lantern Road");
        var seen = foreign.Answers.Requests.Count;
        foreign.User.UserId = Guid.NewGuid();
        await foreign.SayAsync("more", eventId: "$other");
        Assert.AreEqual(seen, foreign.Answers.Requests.Count, "Another reader's list cannot be continued.");
    }

    // ---- B-R4: what is shown, what is saved and what is replayed are one list ---------------------------------------------

    [TestMethod]
    public async Task ATitleOfTwoHundredAndOneCharactersIsAnsweredWithABoundedLineAndNeverThrows()
    {
        var title = new string('T', 201);
        var harness = new ConversationHarness();
        // The id is the source's own, short and whole; only the title is long.
        var source = new SourceRef("fake", "w-long-title", ExternalReferenceEntityType.Work);
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry(title, 4) with { Source = source });

        var reply = await harness.SayAsync("the latest Lantern Road book");

        Assert.AreEqual(ConversationReplyKind.Answer, reply.Reply.Kind);
        Assert.IsFalse(reply.Reply.Text.Contains(title, StringComparison.Ordinal), "The long title is shortened for reading.");
        Assert.IsTrue(reply.Reply.Choices.All(choice => choice.Title.Length <= 200));
        // The identity that selects the record is the source's own id, so the book can still be requested.
        var requested = await harness.SayAsync("request 1 audiobook");
        CollectionAssert.Contains(harness.Tools.Calls, "resolve:w-long-title");
        Assert.HasCount(1, harness.Tools.Created, "The long title did not stop the record being requested.");
        Assert.AreEqual(ConversationReplyKind.RequestCreated, requested.Reply.Kind);
    }

    [TestMethod]
    public async Task EveryNumberShownIsANumberTheSessionKeepsEvenWhenTheAnswerHasMore()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(
            Enumerable.Range(1, 8).Select(index => TestAnswer.Entry("Book " + index, index)).ToArray());

        var reply = await harness.SayAsync("reading order of Lantern Road", transport: ConversationTransports.Matrix, scope: Room);

        Assert.IsLessThanOrEqualTo(5, reply.Reply.Choices.Count, "No more numbers are offered than the conversation can act on.");
        Assert.IsFalse(reply.Reply.Text.Contains("6. ", StringComparison.Ordinal), reply.Reply.Text);
        foreach (var choice in reply.Reply.Choices)
        {
            var asked = await harness.SayAsync($"details {choice.Number}", transport: ConversationTransports.Matrix, scope: Room);
            Assert.AreNotEqual(ConversationReplyKind.NotUnderstood, asked.Reply.Kind, $"Choice {choice.Number} was offered and must work.");
        }

        StringAssert.Contains(reply.Reply.Text, "not shown");
    }

    [TestMethod]
    public async Task FiveAnswersAreAllOfferedAndSixOffersFiveAndSaysOneIsNotShown()
    {
        static ConversationHarness With(int count)
        {
            var harness = new ConversationHarness();
            harness.Answers.Script = _ => TestAnswer.Members(
                Enumerable.Range(1, count).Select(index => TestAnswer.Entry("Book " + index, index)).ToArray());
            return harness;
        }

        var five = await With(5).SayAsync("reading order of Lantern Road");
        var six = await With(6).SayAsync("reading order of Lantern Road");

        Assert.HasCount(5, five.Reply.Choices);
        Assert.IsFalse(five.Reply.Text.Contains("not shown", StringComparison.Ordinal));
        Assert.HasCount(5, six.Reply.Choices);
        StringAssert.Contains(six.Reply.Text, "+1 more");
    }

    [TestMethod]
    public async Task WhenTheBudgetCutsAMatrixListEveryNumberLeftIsOneTheSessionKeepsAndCanRequest()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(
            Enumerable.Range(1, 5).Select(index => TestAnswer.Entry(new string((char)('A' + index), 100) + " " + index, index)).ToArray()) with
        {
            Qualifiers =
            [
                CatalogAnswerCodes.ListNotExhaustive, CatalogAnswerCodes.SourceGaps, CatalogAnswerCodes.DateBasisUnverified,
                CatalogAnswerCodes.IncompleteCoverage
            ]
        };

        var reply = await harness.SayAsync("reading order of Lantern Road", transport: ConversationTransports.Matrix, scope: Room);

        Assert.IsLessThanOrEqualTo(1500, reply.Reply.Text.Length);
        Assert.IsLessThan(5, reply.Reply.Choices.Count, "The budget really did cut the list in this scenario.");
        Assert.IsGreaterThan(0, reply.Reply.Choices.Count, "Some of the list still fits.");
        StringAssert.Contains(reply.Reply.Text, "not shown");
        var highestShown = reply.Reply.Text.Split('\n')
            .Select(line => int.TryParse(line.Split('.')[0], out var number) ? number : 0)
            .Max();
        Assert.AreEqual(reply.Reply.Choices.Count, highestShown, "The text numbers exactly the choices the reply offers.");
        var state = harness.Sessions.Sessions.Values.Single().State;
        Assert.HasCount(reply.Reply.Choices.Count, state.Choices, "The session keeps exactly the numbers it showed.");

        var requested = await harness.SayAsync($"request {reply.Reply.Choices.Count} audiobook", transport: ConversationTransports.Matrix, scope: Room);
        Assert.AreEqual(ConversationReplyKind.RequestCreated, requested.Reply.Kind, requested.Reply.Text);
    }

    [TestMethod]
    public void ARecordedReplyAndASavedStateWrittenBeforeTheListIdStillLoad()
    {
        var reply = System.Text.Json.JsonSerializer.Deserialize<ConversationReply>(
            "{\"Kind\":0,\"Text\":\"hi\",\"Choices\":[],\"Requests\":[],\"SuggestedReplies\":[],\"RequestId\":null,\"Answers\":null}");
        var state = ConversationState.Deserialize("{\"Version\":2,\"Phase\":1,\"Choices\":[]}");

        Assert.IsNotNull(reply);
        Assert.IsNull(reply.ListId);
        Assert.IsNull(state.ListId);
        Assert.AreEqual(0, state.MoreEntries);
    }

    [TestMethod]
    public async Task ARecordedReplyReplaysWithItsChoicesAndQualificationsWhenTheFullFormIsTooLarge()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(
            Enumerable.Range(1, 5).Select(index => TestAnswer.Entry(new string((char)('A' + index), 190) + " “quoted” " + index, index)).ToArray()) with
        {
            Qualifiers = [CatalogAnswerCodes.ListNotExhaustive, CatalogAnswerCodes.SourceGaps, CatalogAnswerCodes.DateBasisUnverified]
        };

        var first = await harness.SayAsync("reading order of Lantern Road", eventId: "$same", transport: ConversationTransports.Web, scope: "web");
        var again = await harness.SayAsync("reading order of Lantern Road", eventId: "$same", transport: ConversationTransports.Web, scope: "web");

        Assert.IsTrue(again.Replayed);
        Assert.HasCount(first.Reply.Choices.Count, again.Reply.Choices, "A replay keeps the actions the first reply offered.");
        StringAssert.Contains(again.Reply.Text, "not a complete list");
        StringAssert.Contains(again.Reply.Text, "Some sources did not answer");
    }
}
