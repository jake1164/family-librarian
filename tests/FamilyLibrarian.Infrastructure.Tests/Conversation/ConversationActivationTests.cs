using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Accounts;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// P4-B: the shared catalog answer is what chat answers with, and what it keeps between turns belongs to the person, the transport
/// and the room it was made in. These tests drive the engine, not the answer rules (which <c>CatalogAnswerPolicyTests</c> covers),
/// so a number, a follow-up or an expansion is shown to select only what the server already kept for this reader.
/// </summary>
[TestClass]
public sealed class ConversationActivationTests
{
    private static async Task<ConversationHarness> OfferingAsync(bool web = false)
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Subject is null
            ? TestAnswer.Offering(TestAnswer.AuthorChoice("Tom Clancy"), TestAnswer.SeriesChoice("Jack Ryan"))
            : TestAnswer.Latest(TestAnswer.Entry("The Hunt for Red October", 1));
        // One conversation: every later message must be in the same transport and scope as the offer.
        harness.DefaultTransport = web ? ConversationTransports.Web : ConversationTransports.Matrix;
        harness.DefaultScope = web ? "web" : "!room:example.test";
        await harness.SayAsync("the latest Tom Clancy book");
        return harness;
    }

    [TestMethod]
    public async Task ANumberSelectsTheRetainedReferenceAndOutOfRangeNumbersAskAgainWithoutALookup()
    {
        var harness = await OfferingAsync();
        var before = harness.Answers.Requests.Count;

        var outOfRange = await harness.SayAsync("7");
        var afterOutOfRange = harness.Answers.Requests.Count;
        var picked = await harness.SayAsync("1");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, outOfRange.Reply.Kind);
        Assert.AreEqual(before, afterOutOfRange, "An out-of-range number spends nothing.");
        Assert.AreEqual(ConversationReplyKind.Answer, picked.Reply.Kind);
        var request = harness.Answers.Requests[^1];
        Assert.AreEqual(CatalogSubjectKind.Author, request.Subject!.Kind);
        Assert.AreEqual(TestAnswer.Author, request.Subject.Native);
        Assert.IsNotNull(request.ContextRootId, "The choice continues on the root it was retained under.");
        Assert.IsNotEmpty(picked.Reply.Text);
    }

    [TestMethod]
    public async Task AnExpiredChoiceAsksTheReaderToStartAgainAndMakesNoCall()
    {
        var harness = await OfferingAsync();
        var before = harness.Answers.Requests.Count;

        harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(11);
        var reply = await harness.SayAsync("1");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, reply.Reply.Kind);
        StringAssert.Contains(reply.Reply.Text, "expired");
        Assert.AreEqual(before, harness.Answers.Requests.Count, "An expired list is never re-fetched or guessed from.");
    }

    [TestMethod]
    public async Task AnotherReaderCannotUseThisReadersNumber()
    {
        var harness = await OfferingAsync();
        var before = harness.Answers.Requests.Count;

        harness.User.UserId = Guid.NewGuid();
        var reply = await harness.SayAsync("1", eventId: "$other");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, reply.Reply.Kind);
        Assert.AreEqual(before, harness.Answers.Requests.Count, "A number from another conversation selects nothing.");
    }

    [TestMethod]
    public async Task HelpAndStatusKeepTheOfferAndCancelClearsIt()
    {
        var harness = await OfferingAsync();

        await harness.SayAsync("help");
        await harness.SayAsync("status");
        var picked = await harness.SayAsync("2");

        Assert.AreEqual(CatalogSubjectKind.Series, harness.Answers.Requests[^1].Subject!.Kind, "Informational turns keep the offer usable.");
        Assert.AreEqual(ConversationReplyKind.Answer, picked.Reply.Kind);

        var cancelled = await OfferingAsync();
        var before = cancelled.Answers.Requests.Count;
        await cancelled.SayAsync("cancel");
        var afterCancel = await cancelled.SayAsync("1");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, afterCancel.Reply.Kind);
        Assert.AreEqual(before, cancelled.Answers.Requests.Count, "Cancel clears the actionable reference.");
    }

    [TestMethod]
    public async Task AnUnrelatedQuestionStartsItsOwnLookupAndTheOldNumberNoLongerApplies()
    {
        var harness = await OfferingAsync();
        harness.Tools.Results["Weather"] = new ConversationSearchResult([FakeTools.Choice("w1", "Weather")], false, []);
        var before = harness.Answers.Requests.Count;

        var answered = await harness.SayAsync("find Weather");
        var stale = await harness.SayAsync("2");

        Assert.AreEqual(ConversationReplyKind.Choices, answered.Reply.Kind);
        Assert.AreEqual(before, harness.Answers.Requests.Count, "An ordinary search does not ask the answer service.");
        Assert.AreEqual(ConversationReplyKind.NotUnderstood, stale.Reply.Kind, "A new list replaces the old one; a stale number does nothing.");
        Assert.AreEqual(before, harness.Answers.Requests.Count);
    }

    [TestMethod]
    public async Task AnOrderTheBooksDisagreeOnIsOfferedAsNumbersAndTheChosenOneIsApplied()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Order == SeriesOrderingKind.Unspecified
            ? new CatalogAnswer
            {
                Operation = request.Operation,
                Outcome = CatalogAnswerOutcome.Clarification,
                Subject = CatalogSubjectRef.FromSource(CatalogSubjectKind.Series, "The Lantern Road", TestAnswer.Series),
                AvailableOrders = [SeriesOrderingKind.Publication, SeriesOrderingKind.Reading],
                Reasons = [CatalogAnswerCodes.OrderAmbiguous],
                Today = new DateOnly(2026, 11, 18)
            }
            : TestAnswer.Members(TestAnswer.Entry("The Lantern Road", 1));

        var asked = await harness.SayAsync("reading order of Lantern Road");
        StringAssert.Contains(asked.Reply.Text, "publication order");
        StringAssert.Contains(asked.Reply.Text, "reading order");

        var answered = await harness.SayAsync("2");

        Assert.AreEqual(ConversationReplyKind.Answer, answered.Reply.Kind);
        Assert.AreEqual(SeriesOrderingKind.Reading, harness.Answers.Requests[^1].Order);
        Assert.AreEqual(TestAnswer.Series, harness.Answers.Requests[^1].Subject!.Native);
    }

    [TestMethod]
    public async Task AnAuthorBibliographyQuestionOffersTheAuthorAndThenListsTheirWorks()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Subject is null
            ? TestAnswer.Offering(TestAnswer.AuthorChoice("Becky Chambers"))
            : TestAnswer.Members(TestAnswer.Entry("The Long Way to a Small, Angry Planet", 1));

        var offered = await harness.SayAsync("what has Becky Chambers written?");
        var works = await harness.SayAsync("1");

        Assert.AreEqual(CatalogAnswerOperation.AuthorWorks, harness.Answers.Requests[0].Operation);
        Assert.AreEqual("Becky Chambers", harness.Answers.Requests[0].Text);
        Assert.AreEqual(ConversationReplyKind.Clarification, offered.Reply.Kind);
        Assert.AreEqual(CatalogSubjectKind.Author, harness.Answers.Requests[^1].Subject!.Kind);
        Assert.AreEqual(ConversationReplyKind.Answer, works.Reply.Kind);
        StringAssert.Contains(works.Reply.Text, "The Long Way to a Small, Angry Planet");
    }

    [TestMethod]
    public async Task FramingAnAuthorQuestionAsASeriesQuestionIsOfferedAsChoicesAndNeverPickedForTheReader()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Offering(TestAnswer.AuthorChoice("Tom Clancy")) with { Reasons = [CatalogAnswerCodes.AuthorChoices] };

        var reply = await harness.SayAsync("latest Tom Clancy book");

        StringAssert.Contains(reply.Reply.Text, "belongs to an author");
        Assert.HasCount(1, reply.Reply.Choices);
        Assert.AreEqual("Tom Clancy", reply.Reply.Choices[0].Title);
        Assert.IsNotNull(reply.Reply.Answers, "The page receives the same choice as a card.");
    }

    // ---- editions --------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task EditionsExpandsTheRecordTheReaderSelectedAndNothingWhenNothingWasSelected()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        harness.Answers.EditionsAnswer = new CatalogEditionsAnswer(
            CatalogAnswerOutcome.Qualified,
            TestAnswer.Work("Road's End"),
            "Road's End",
            [new CatalogEditionTitle("OL1M", "Road's End", "en", "title"), new CatalogEditionTitle("OL2M", "Road's End (Large Print)", null, "title")],
            [CatalogAnswerCodes.ListNotExhaustive],
            [],
            CatalogAnswerUsage.None,
            "fake");
        await harness.SayAsync("the latest Lantern Road book");

        var reply = await harness.SayAsync("editions");

        Assert.AreEqual(ConversationReplyKind.Editions, reply.Reply.Kind);
        var request = harness.Answers.EditionRequests.Single();
        Assert.AreEqual(TestAnswer.Work("Road's End"), request.Entry.Native);
        StringAssert.Contains(reply.Reply.Text, "Road's End (Large Print)");
        StringAssert.Contains(reply.Reply.Text, "language not stated");
        StringAssert.Contains(reply.Reply.Text, "not alternative titles, translations or identity proof");

        // A conversation with no retained answer calls nothing at all.
        var fresh = new ConversationHarness();
        var nothing = await fresh.SayAsync("editions 1");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, nothing.Reply.Kind);
        Assert.IsEmpty(fresh.Answers.EditionRequests);
    }

    [TestMethod]
    public async Task EditionsUsesTheSameNumberingAsTheAnswerAndRejectsOneTheAnswerDidNotOffer()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(TestAnswer.Entry("Alder", 1), TestAnswer.Entry("Birch", 2));
        await harness.SayAsync("reading order of Lantern Road");

        await harness.SayAsync("editions 2");

        Assert.AreEqual(TestAnswer.Work("Birch"), harness.Answers.EditionRequests.Single().Entry.Native);
    }

    // ---- authorization ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAccountWithdrawnBetweenTurnsIsRefusedBeforeAnyAnswer()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        await harness.SayAsync("the latest Lantern Road book");
        var before = harness.Answers.Requests.Count;

        harness.Accounts.Status = UserStatus.Invited;
        var reply = await harness.SayAsync("the latest Lantern Road book");

        Assert.AreEqual(ConversationReplyKind.Unauthenticated, reply.Reply.Kind);
        Assert.AreEqual(before, harness.Answers.Requests.Count, "No catalog answer is asked for a withdrawn account.");
    }

    // ---- rendering and budget --------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AMatrixAnswerKeepsItsQualificationAndNextStepInsideTheMatrixLength()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(
            Enumerable.Range(1, 12)
                .Select(index => TestAnswer.Entry("A book with a deliberately long title, number " + index, index))
                .ToArray()) with
        {
            Qualifiers = [CatalogAnswerCodes.ListNotExhaustive, CatalogAnswerCodes.SourceGaps, CatalogAnswerCodes.DateBasisUnverified],
            Groups =
            [
                new CatalogAnswerGroup("unnumbered", [TestAnswer.Entry("An unnumbered companion")])
            ]
        };

        var reply = await harness.SayAsync("reading order of Lantern Road", transport: ConversationTransports.Matrix, scope: "!room:example.test");

        Assert.IsLessThanOrEqualTo(1500, reply.Reply.Text.Length, "The answer is rendered to fit Matrix without being cut mid-sentence.");
        StringAssert.Contains(reply.Reply.Text, "not a complete list");
        StringAssert.Contains(reply.Reply.Text, "Some sources did not answer");
        StringAssert.Contains(reply.Reply.Text, "Reply with a number for details or to request it");
        StringAssert.Contains(reply.Reply.Text, "not shown");
        Assert.IsFalse(reply.Reply.Text.Contains("list-not-exhaustive", StringComparison.Ordinal), "An internal code is never shown as prose.");
        Assert.IsLessThanOrEqualTo(
            MatrixReplyFormatter.MaxLength, MatrixReplyFormatter.ToText(reply.Reply).Length, "The formatter has nothing left to cut.");
    }

    [TestMethod]
    public async Task TheSameShortAnswerReadsTheSameOnBothTransports()
    {
        static ConversationHarness Harness()
        {
            var harness = new ConversationHarness();
            harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
            return harness;
        }

        var web = await Harness().SayAsync("the latest Lantern Road book", transport: ConversationTransports.Web, scope: "web");
        var matrix = await Harness().SayAsync("the latest Lantern Road book", transport: ConversationTransports.Matrix, scope: "!room:example.test");

        Assert.AreEqual(web.Reply.Text, matrix.Reply.Text, "One answer, one wording; only the presentation budget differs.");
        Assert.AreEqual(
            string.Join("|", web.Reply.Choices.Select(choice => choice.Title)),
            string.Join("|", matrix.Reply.Choices.Select(choice => choice.Title)));
    }

    [TestMethod]
    public async Task AnAnswerWithNoRecordsNeverLeavesAnEmptyAnswer()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => TestAnswer.Nothing(request.Operation, CatalogAnswerCodes.NoEntries);

        var reply = await harness.SayAsync("reading order of Lantern Road");

        Assert.IsFalse(string.IsNullOrWhiteSpace(reply.Reply.Text));
        StringAssert.Contains(reply.Reply.Text, "held nothing to list");
        Assert.IsEmpty(reply.Reply.Choices);
    }
}
