using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// CHAT-ANSWER-1: chat must not give confident wrong answers or lose its context. Cases come from the lab chat matrix
/// (<c>.ai_docs/chat</c>, T01-T20): follow-ups about "it", honest answers about unreleased books and release years, series
/// wording the parser missed, and a model that must not override the fixed rules.
/// </summary>
[TestClass]
public sealed class ConversationAnswerTests
{
    private static SeriesBook Book(string series, decimal? position, string title) =>
        new(series, position, title, "Mara Quill", "fake", title.Replace(' ', '-').ToLowerInvariant());

    private static SeriesLookupResult Found(string series, params SeriesBook[] books) =>
        new(SeriesLookupKind.Found, series, books, [], books.Where(book => book.Position is not null).Select(book => book.Position!.Value).ToArray());

    private sealed class FakeInterpreter(BookIntentInterpretation answer) : IBookIntentInterpreter
    {
        public Task<BookIntentInterpretation?> InterpretAsync(Guid userId, string text, string? previousSearch, CancellationToken cancellationToken) =>
            Task.FromResult<BookIntentInterpretation?>(answer);
    }

    // ---- the lab matrix, as the parser sees it -----------------------------------------------------------------------

    /// <summary>
    /// Every input of the lab chat matrix (T01-T20) and how it is understood. The parser decides which operation runs, so this
    /// pins that "latest", "next", "upcoming", "book N" and "it" each take a different one. Whether the catalog then has the
    /// answer is a data question the lab re-run settles.
    /// </summary>
    [TestMethod]
    [DataRow("What is the latest Jack Ryan book?", ConversationCommandKind.Series, null)]
    [DataRow("What is the next Jack Ryan book?", ConversationCommandKind.Release, "next")]
    [DataRow("Show me upcoming Jack Ryan books", ConversationCommandKind.Release, "upcoming")]
    [DataRow("What is the 3rd Murderbot book?", ConversationCommandKind.Series, null)]
    [DataRow("What is book 5 of The Expanse?", ConversationCommandKind.Series, null)]
    [DataRow("What book comes after Nemesis Games?", ConversationCommandKind.Series, null)]
    [DataRow("What is the latest novel in The Expanse?", ConversationCommandKind.Series, null)]
    [DataRow("What is the third Harry Potter book?", ConversationCommandKind.Series, null)]
    [DataRow("Find books in the Jack Reacher series", ConversationCommandKind.Series, null)]
    [DataRow("Find the Jack Ryan Jr. books", ConversationCommandKind.Series, null)]
    [DataRow("What is the latest original Harry Potter novel?", ConversationCommandKind.Series, null)]
    [DataRow("Are there any upcoming novels in the original Harry Potter series?", ConversationCommandKind.Release, "upcoming")]
    [DataRow("What is the next Expanse novel?", ConversationCommandKind.Release, "next")]
    [DataRow("When is The Winds of Winter coming out?", ConversationCommandKind.Release, "when")]
    [DataRow("Show me Expanse novels released after 2030", ConversationCommandKind.Release, "year")]
    [DataRow("What is book 25 of The Expanse?", ConversationCommandKind.Series, null)]
    [DataRow("What comes out next?", ConversationCommandKind.Reference, "next")]
    [DataRow("Is it available as an audiobook?", ConversationCommandKind.Reference, "availability")]
    [DataRow("Can I request it?", ConversationCommandKind.Reference, "request")]
    public void EveryMatrixInputTakesTheOperationItsWordingCallsFor(string text, ConversationCommandKind kind, string? action)
    {
        var parsed = ConversationParser.Parse(text);

        Assert.AreEqual(kind, parsed.Kind, text);
        Assert.AreEqual(action, parsed.Action, text);
    }

    [TestMethod]
    [DataRow("What is the latest Jack Ryan book?", "Jack Ryan", -1)]
    [DataRow("What is the latest novel in The Expanse?", "Expanse", -1)]
    [DataRow("What is the 3rd Murderbot book?", "Murderbot", 3)]
    [DataRow("What is the third Harry Potter book?", "Harry Potter", 3)]
    [DataRow("What is book 5 of The Expanse?", "Expanse", 5)]
    [DataRow("What is book 25 of The Expanse?", "Expanse", 25)]
    [DataRow("What is the latest original Harry Potter novel?", "Harry Potter", -1)]
    public void SeriesMatrixInputsKeepTheirSeriesAndPosition(string text, string series, int position)
    {
        var parsed = ConversationParser.Parse(text);

        Assert.AreEqual(series, parsed.SeriesName, ignoreCase: true, text);
        Assert.AreEqual((decimal)position, parsed.SeriesPosition, text);
    }

    // ---- parser: references to the book in focus ------------------------------------------------------------------

    [TestMethod]
    [DataRow("request it", "request")]
    [DataRow("Can I request it?", "request")]
    [DataRow("could you get that one", "request")]
    [DataRow("I want it", "request")]
    [DataRow("Is it available as an audiobook?", "availability")]
    [DataRow("is that in the library", "availability")]
    [DataRow("do we have it", "availability")]
    [DataRow("tell me more about it", "details")]
    [DataRow("What comes out next?", "next")]
    [DataRow("what's next", "next")]
    [DataRow("and the next one?", "next")]
    public void ReferencesToTheBookInFocusAreRecognized(string text, string action)
    {
        var parsed = ConversationParser.Parse(text);

        Assert.AreEqual(ConversationCommandKind.Reference, parsed.Kind, text);
        Assert.AreEqual(action, parsed.Action, text);
    }

    [TestMethod]
    public void AReferenceCarriesAnyFormatTheSenderNamed()
    {
        CollectionAssert.AreEqual(
            new[] { RequestMediaType.Audiobook }, ConversationParser.Parse("Is it available as an audiobook?").Formats!.ToArray());
        CollectionAssert.AreEqual(
            new[] { RequestMediaType.Audiobook }, ConversationParser.Parse("request it as an audiobook").Formats!.ToArray());
        Assert.IsNull(ConversationParser.Parse("Can I request it?").Formats);
    }

    [TestMethod]
    [DataRow("do we have Dune", ConversationCommandKind.Library)]
    [DataRow("request 2", ConversationCommandKind.Request)]
    [DataRow("details 2", ConversationCommandKind.Details)]
    [DataRow("Find Italy", ConversationCommandKind.Search)]
    public void NamedBooksAndNumbersAreNeverTreatedAsReferences(string text, ConversationCommandKind expected) =>
        Assert.AreEqual(expected, ConversationParser.Parse(text).Kind, text);

    // ---- parser: things chat cannot see -------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("Show me upcoming Jack Ryan books", "upcoming")]
    [DataRow("Are there any upcoming novels in the original Harry Potter series?", "upcoming")]
    [DataRow("any forthcoming Murderbot novels", "upcoming")]
    [DataRow("Show me Expanse novels released after 2030", "year")]
    [DataRow("books published in 1999", "year")]
    public void UnreleasedBooksAndReleaseYearsAreRecognizedAsOutsideWhatChatCanSee(string text, string action)
    {
        var parsed = ConversationParser.Parse(text);

        Assert.AreEqual(ConversationCommandKind.Release, parsed.Kind, text);
        Assert.AreEqual(action, parsed.Action, text);
    }

    [TestMethod]
    public void AReleaseDateQuestionKeepsTheTitleSoItCanStillBeLookedUp()
    {
        var parsed = ConversationParser.Parse("When is The Winds of Winter coming out?");

        Assert.AreEqual(ConversationCommandKind.Release, parsed.Kind);
        Assert.AreEqual("when", parsed.Action);
        Assert.AreEqual("The Winds of Winter", parsed.Text);
    }

    [TestMethod]
    public void TheNextBookOfASeriesWithNoNamedBookMeansTheNextReleaseAndKeepsTheSeries()
    {
        var parsed = ConversationParser.Parse("What is the next Expanse novel?");

        Assert.AreEqual(ConversationCommandKind.Release, parsed.Kind);
        Assert.AreEqual("next", parsed.Action);
        Assert.AreEqual("Expanse", parsed.SeriesName);
    }

    [TestMethod]
    [DataRow("what is the next book in Jack Ryan after Debt of Honor")]
    [DataRow("what's the next Jack Ryan book after Debt of Honor?")]
    public void ANextQuestionAnchoredOnANamedBookStaysASeriesQuestion(string text) =>
        Assert.AreEqual(ConversationCommandKind.Series, ConversationParser.Parse(text).Kind, text);

    [TestMethod]
    [DataRow("Coming Soon")]
    [DataRow("Upcoming")]
    [DataRow("Published in 1984")]
    public void ShortTitlesThatHappenToUseThoseWordsAreStillSearched(string text) =>
        Assert.AreEqual(ConversationCommandKind.Search, ConversationParser.Parse(text).Kind, text);

    // ---- series wording --------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("What is book 5 of The Expanse?", "Expanse", 5)]
    [DataRow("what's book 25 of the expanse", "expanse", 25)]
    public void AQuestionOpeningBeforeBookNIsStillASeriesPosition(string text, string series, int position)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var found), text);
        Assert.AreEqual(series, name, ignoreCase: true);
        Assert.AreEqual((decimal)position, found);
    }

    [TestMethod]
    [DataRow("What is the latest original Harry Potter novel?")]
    [DataRow("what is the newest main Harry Potter book")]
    public void AQualifierWordIsNotPartOfTheSeriesName(string text)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var position), text);
        Assert.AreEqual("Harry Potter", name);
        Assert.AreEqual(SeriesPositionResolver.LastPosition, position);
    }

    [TestMethod]
    public void AParentheticalInASeriesNameDoesNotMakeItAmbiguousWithItself()
    {
        SeriesBook[] known =
        [
            Book("The Expanse (Chronological)", 1.1m, "The Butcher of Anderson Station"),
            Book("The Expanse (Chronological)", 9m, "Leviathan Falls"),
            Book("The Expanse Origins", 1m, "The Expanse Origins: James Holden")
        ];

        var latest = SeriesPositionResolver.Resolve(known, "Expanse", SeriesPositionResolver.LastPosition, 5);

        Assert.AreEqual(SeriesLookupKind.Found, latest.Kind);
        Assert.AreEqual("Leviathan Falls", latest.Books.Single().Title);
    }

    // ---- honest wording ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheLatestBookIsOnlyEverTheHighestOneTheCatalogShowed()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found("The Lantern Road", Book("The Lantern Road", 4, "Road's End"));

        var reply = await harness.SayAsync("What is the latest Lantern Road book?");

        Assert.AreEqual(ConversationReplyKind.Choices, reply.Reply.Kind);
        StringAssert.Contains(reply.Reply.Text, "highest-numbered");
        StringAssert.Contains(reply.Reply.Text, "may not list every book");
        Assert.IsFalse(reply.Reply.Text.Contains("book 4 is", StringComparison.Ordinal), "It must not be worded as a settled fact.");
    }

    [TestMethod]
    public async Task AListingAndAMissingBookBothSayTheCatalogMayBeIncomplete()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found(
            "The Lantern Road", Book("The Lantern Road", 1, "The Lantern Road"), Book("The Lantern Road", 2, "Lanterns at Dusk"));
        var listing = await harness.SayAsync("reading order of Lantern Road");

        harness.Tools.SeriesResults["Lantern Road"] = new SeriesLookupResult(SeriesLookupKind.PositionMissing, "The Lantern Road", [], [], [1m, 2m]);
        var missing = await harness.SayAsync("the fifth Lantern Road book");

        StringAssert.Contains(listing.Reply.Text, "in order");
        StringAssert.Contains(listing.Reply.Text, "may be incomplete");
        StringAssert.Contains(missing.Reply.Text, "I know of 1, 2");
        StringAssert.Contains(missing.Reply.Text, "may not list every book");
    }

    [TestMethod]
    public async Task ASeriesQuestionWithNoKnownSeriesSearchesTheSeriesNameAndSaysItCouldNotConfirmTheOrder()
    {
        var harness = new ConversationHarness();
        harness.Tools.Results["Murderbot"] = new ConversationSearchResult([FakeTools.Choice("m1", "Rogue Protocol")], false, []);

        var reply = await harness.SayAsync("What is the 3rd Murderbot book?");

        CollectionAssert.Contains(harness.Tools.Calls, "search:Murderbot");
        Assert.AreEqual(ConversationReplyKind.Choices, reply.Reply.Kind);
        StringAssert.Contains(reply.Reply.Text, "couldn't confirm");
        Assert.IsFalse(harness.Tools.Calls.Contains("search:What is the 3rd Murderbot book?"), "The whole sentence is never the search.");
    }

    [TestMethod]
    public async Task AnUnansweredAfterQuestionSearchesTheTitleInsteadOfTheSentence()
    {
        var harness = new ConversationHarness();
        harness.Tools.Results["Nemesis Games"] = new ConversationSearchResult([FakeTools.Choice("n1", "Nemesis Games")], false, []);

        var reply = await harness.SayAsync("What book comes after Nemesis Games?");

        CollectionAssert.Contains(harness.Tools.Calls, "search:Nemesis Games");
        StringAssert.Contains(reply.Reply.Text, "couldn't work out what comes after");
    }

    [TestMethod]
    public async Task APlainTitleThatLooksLikeASeriesQuestionIsStillSearchedAsTyped()
    {
        var harness = new ConversationHarness();
        harness.Tools.Results["The First Book of Samuel"] = new ConversationSearchResult([FakeTools.Choice("s1", "The First Book of Samuel")], false, []);

        await harness.SayAsync("The First Book of Samuel");

        CollectionAssert.Contains(harness.Tools.Calls, "search:The First Book of Samuel");
    }

    // ---- unreleased books and release years --------------------------------------------------------------------------

    [TestMethod]
    public async Task UpcomingBooksAndReleaseYearsAreSaidToBeOutsideWhatChatCanSeeWithoutSearching()
    {
        var harness = new ConversationHarness();

        var upcoming = await harness.SayAsync("Show me upcoming Jack Ryan books");
        var year = await harness.SayAsync("Show me Expanse novels released after 2030");

        StringAssert.Contains(upcoming.Reply.Text, "can't see");
        StringAssert.Contains(upcoming.Reply.Text, "haven't been published");
        StringAssert.Contains(year.Reply.Text, "release year");
        Assert.IsFalse(harness.Tools.Calls.Any(call => call.StartsWith("search:", StringComparison.Ordinal)), "No irrelevant results are shown.");
    }

    [TestMethod]
    public async Task AReleaseDateQuestionLooksTheTitleUpAndSaysItCannotConfirmADate()
    {
        var harness = new ConversationHarness();
        harness.Tools.Results["The Winds of Winter"] = new ConversationSearchResult([FakeTools.Choice("w1", "The Winds of Winter", "George R.R. Martin")], false, []);

        var reply = await harness.SayAsync("When is The Winds of Winter coming out?");

        Assert.AreEqual(ConversationReplyKind.Choices, reply.Reply.Kind);
        StringAssert.Contains(reply.Reply.Text, "can't confirm release dates");
        Assert.HasCount(1, reply.Reply.Choices);
    }

    [TestMethod]
    public async Task TheNextBookOfASeriesSaysItCannotSeeReleasesAndOffersTheLatestItKnows()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Expanse"] = Found("The Expanse", Book("The Expanse", 9, "Leviathan Falls"));

        var known = await harness.SayAsync("What is the next Expanse novel?");
        var none = await new ConversationHarness().SayAsync("What is the next Expanse novel?");

        CollectionAssert.Contains(harness.Tools.Calls, "series:Expanse|-1");
        StringAssert.Contains(known.Reply.Text, "can't see books that haven't been published");
        StringAssert.Contains(known.Reply.Text, "Leviathan Falls");
        StringAssert.Contains(none.Reply.Text, "can't see books that haven't been published");
    }

    // ---- follow-ups about "it" ---------------------------------------------------------------------------------------

    private static async Task<ConversationHarness> WithOneBookInFocusAsync()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found("The Lantern Road", Book("The Lantern Road", 3, "The Last Lantern"));
        await harness.SayAsync("the third Lantern Road book");
        return harness;
    }

    [TestMethod]
    public async Task RequestItStartsTheOrdinaryRequestFlowForTheOneBookInFocus()
    {
        var harness = await WithOneBookInFocusAsync();

        var asked = await harness.SayAsync("Can I request it?");
        var created = await harness.SayAsync("audiobook");

        Assert.AreEqual(ConversationReplyKind.AskFormat, asked.Reply.Kind);
        StringAssert.Contains(asked.Reply.Text, "The Last Lantern");
        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.HasCount(1, harness.Tools.Created);
    }

    [TestMethod]
    public async Task RequestItWithAFormatRequestsItAtOnceButNeverWithoutABookInFocus()
    {
        var focused = await WithOneBookInFocusAsync();
        var requested = await focused.SayAsync("request it as an audiobook");

        var empty = new ConversationHarness();
        var nothing = await empty.SayAsync("request it");

        Assert.AreEqual(ConversationReplyKind.RequestCreated, requested.Reply.Kind);
        Assert.AreEqual(ConversationReplyKind.NotUnderstood, nothing.Reply.Kind);
        Assert.IsEmpty(empty.Tools.Created);
    }

    [TestMethod]
    public async Task ItNeverPicksWhenSeveralBooksWereOffered()
    {
        var harness = new ConversationHarness().WithResults("The Lantern Road");
        await harness.SayAsync("Find The Lantern Road");

        var reply = await harness.SayAsync("request it");

        Assert.AreEqual(ConversationReplyKind.NotUnderstood, reply.Reply.Kind);
        StringAssert.Contains(reply.Reply.Text, "number");
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task IsItAvailableChecksTheLibraryForTheBookInFocus()
    {
        var harness = await WithOneBookInFocusAsync();

        var reply = await harness.SayAsync("Is it available as an audiobook?");

        Assert.AreEqual(ConversationReplyKind.Library, reply.Reply.Kind);
        Assert.IsTrue(harness.Tools.Calls.Any(call => call.StartsWith("library:The Last Lantern", StringComparison.Ordinal)));
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task WhatComesNextAsksTheSeriesWhatFollowsTheBookInFocus()
    {
        var harness = await WithOneBookInFocusAsync();

        var reply = await harness.SayAsync("What comes out next?");

        CollectionAssert.Contains(harness.Tools.Calls, "series:|all|after:The Last Lantern");
        Assert.AreNotEqual(ConversationReplyKind.Status, reply.Reply.Kind);
        StringAssert.Contains(reply.Reply.Text, "The Last Lantern");
    }

    [TestMethod]
    public async Task TheBookInFocusSurvivesAFollowUpThatNeedsNoAction()
    {
        var harness = await WithOneBookInFocusAsync();
        await harness.SayAsync("What comes out next?");

        var reply = await harness.SayAsync("Can I request it?");

        Assert.AreEqual(ConversationReplyKind.AskFormat, reply.Reply.Kind, "The book is still in focus after the earlier follow-up.");
    }

    // ---- the rate limit only counts messages that do work ------------------------------------------------------------------

    [TestMethod]
    public async Task ASessionAllowsTwentyFourTurnsBeforeTheContextIsCleared()
    {
        Assert.AreEqual(24, new ConversationOptions().MaxTurnsPerSession);

        var harness = new ConversationHarness().WithResults("The Lantern Road");
        for (var turn = 0; turn < 24; turn++)
        {
            var reply = await harness.SayAsync("Find The Lantern Road");
            Assert.AreEqual(ConversationReplyKind.Choices, reply.Reply.Kind, $"Turn {turn + 1} is still answered.");
        }

        var cleared = await harness.SayAsync("Find The Lantern Road");

        Assert.AreEqual(ConversationReplyKind.Throttled, cleared.Reply.Kind, "The 25th counted turn clears the conversation.");
    }

    [TestMethod]
    public async Task ControlMessagesAreNotCountedAgainstTheRateLimitButBookMessagesAre()
    {
        var harness = new ConversationHarness().WithResults("The Lantern Road");

        await harness.SayAsync("cancel");
        await harness.SayAsync("help");
        await harness.SayAsync("status");
        await harness.SayAsync("   ");
        await harness.SayAsync("Find The Lantern Road");
        await harness.SayAsync("1");
        await harness.SayAsync("request it");

        CollectionAssert.AreEqual(CountedPattern, harness.Limiter.Counted);
    }

    private static readonly bool[] CountedPattern = [false, false, false, false, true, true, true];

    [TestMethod]
    public async Task ARefusedBookMessageIsAnswerRateLimitedAndWasOneThatCounts()
    {
        var harness = new ConversationHarness();
        harness.Limiter.Deny = true;

        var searched = await harness.SayAsync("Find The Lantern Road");

        Assert.AreEqual(ConversationReplyKind.RateLimited, searched.Reply.Kind);
        Assert.IsTrue(harness.Limiter.Counted.Single());
    }

    // ---- the model cannot override the fixed rules or wipe the conversation --------------------------------------------

    private static ConversationHarness WithModel(BookIntent intent) =>
        new() { Interpreter = new FakeInterpreter(new BookIntentInterpretation(intent, null, null)) };

    [TestMethod]
    public async Task AModelOffTopicVerdictIsIgnoredWhenTheMessageIsPlainlyAboutBooks()
    {
        var harness = WithModel(BookIntent.OutOfDomain);

        var reply = await harness.SayAsync("Are there any novels about a lighthouse keeper in a series?");

        Assert.AreNotEqual(ConversationReplyKind.OutOfDomain, reply.Reply.Kind);
    }

    [TestMethod]
    public async Task AModelStatusVerdictIsIgnoredWhenTheMessageIsNotAboutRequests()
    {
        var harness = WithModel(BookIntent.CheckStatus);

        var reply = await harness.SayAsync("What comes out in the lighthouse keeper series soon");

        Assert.AreNotEqual(ConversationReplyKind.Status, reply.Reply.Kind);
        Assert.IsFalse(harness.Tools.Calls.Contains("status"));
    }

    [TestMethod]
    public async Task AModelStatusOrOffTopicReplyDoesNotClearTheBooksBeingDiscussed()
    {
        var harness = new ConversationHarness { Interpreter = new FakeInterpreter(new BookIntentInterpretation(BookIntent.OutOfDomain, null, null)) }
            .WithResults("The Lantern Road");
        await harness.SayAsync("Find The Lantern Road");

        var off = await harness.SayAsync("what is the capital of france and why is it so big");
        var picked = await harness.SayAsync("2");

        Assert.AreEqual(ConversationReplyKind.OutOfDomain, off.Reply.Kind);
        Assert.AreEqual(ConversationReplyKind.AskFormat, picked.Reply.Kind, "The earlier list must still be there.");
    }
}
