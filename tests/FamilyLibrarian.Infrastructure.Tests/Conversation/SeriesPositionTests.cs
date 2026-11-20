using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Catalog.Answers;
using FamilyLibrarian.Application.Conversation;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>M2-4: reading "the third book of X" and resolving it only from series facts the catalog actually has.</summary>
[TestClass]
public sealed class SeriesPositionTests
{
    private static SeriesBook Book(string series, decimal? position, string title, string authors = "Mara Quill") =>
        new(series, position, title, authors, "demo", title.Replace(' ', '-').ToLowerInvariant());

    private static readonly SeriesBook[] Lanterns =
    [
        Book("The Lantern Road", 1, "The Lantern Road"),
        Book("The Lantern Road", 2, "Lanterns at Dusk"),
        Book("The Lantern Road", 3, "The Last Lantern"),
        Book("The Lantern Road", 4, "Road's End")
    ];

    [TestMethod]
    [DataRow("the third Lantern Road book", "Lantern Road", 3)]
    [DataRow("The third book of The Lantern Road", "Lantern Road", 3)]
    [DataRow("what is the 2nd book in the Lantern Road series?", "Lantern Road", 2)]
    [DataRow("book 4 of Lantern Road", "Lantern Road", 4)]
    [DataRow("Lantern Road book 1", "Lantern Road", 1)]
    [DataRow("lantern road, book 2", "lantern road", 2)]
    [DataRow("Lantern Road #3", "Lantern Road", 3)]
    public void PositionWordingIsRecognized(string text, string series, int position)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var parsed), text);
        Assert.AreEqual(series, name);
        Assert.AreEqual(position, parsed);
    }

    [TestMethod]
    [DataRow("the last book of Lantern Road")]
    [DataRow("latest book in the Lantern Road series")]
    public void TheLatestBookIsRecognized(string text)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var position));
        Assert.AreEqual("Lantern Road", name.Replace("The ", "", StringComparison.Ordinal));
        Assert.AreEqual(SeriesPositionResolver.LastPosition, position);
    }

    [TestMethod]
    [DataRow("reading order of Lantern Road", "Lantern Road")]
    [DataRow("what are the books in the Lantern Road series", "Lantern Road")]
    [DataRow("Lantern Road reading order", "Lantern Road")]
    public void AListingRequestHasNoPosition(string text, string series)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var position), text);
        Assert.AreEqual(series, name);
        Assert.IsNull(position);
    }

    [TestMethod]
    [DataRow("The Lantern Road")]
    [DataRow("Fahrenheit 451")]
    [DataRow("The Third Policeman")]
    [DataRow("Weather by Jenny Offill")]
    [DataRow("book 99999 of x")]
    public void OrdinaryTitlesAreNotSeriesQuestions(string text) =>
        Assert.IsFalse(SeriesParser.TryParse(text, out _, out _), text);

    [TestMethod]
    public void TheParserRoutesSeriesWordingAndKeepsRequestIntentAndFormat()
    {
        var parsed = ConversationParser.Parse("Please get the third Lantern Road book as an audiobook");

        Assert.AreEqual(ConversationCommandKind.Series, parsed.Kind);
        Assert.AreEqual("Lantern Road", parsed.SeriesName);
        Assert.AreEqual(3m, parsed.SeriesPosition);
        Assert.IsTrue(parsed.RequestIntent);
        Assert.AreEqual("Audiobook", string.Join(",", parsed.Formats!));
    }

    [TestMethod]
    public void TheBookAtAPositionIsFoundOnlyWhenTheCatalogKnowsIt()
    {
        var found = SeriesPositionResolver.Resolve(Lanterns, "lantern road", 3, 5);
        var missing = SeriesPositionResolver.Resolve(Lanterns.Where(book => book.Position != 3), "lantern road", 3, 5);

        Assert.AreEqual(SeriesLookupKind.Found, found.Kind);
        Assert.AreEqual("The Last Lantern", found.Books.Single().Title);
        Assert.AreEqual(SeriesLookupKind.PositionMissing, missing.Kind, "A position is never guessed.");
        Assert.AreEqual("1,2,4", string.Join(",", missing.KnownPositions));
    }

    [TestMethod]
    public void TheLatestBookIsTheHighestKnownPosition()
    {
        var latest = SeriesPositionResolver.Resolve(Lanterns, "Lantern Road", SeriesPositionResolver.LastPosition, 5);

        Assert.AreEqual("Road's End", latest.Books.Single().Title);
    }

    [TestMethod]
    public void AListingIsOrderedBoundedAndSkipsBooksWithNoNumber()
    {
        var all = Lanterns.Append(Book("The Lantern Road", null, "Lantern Road Companion")).Reverse();

        var listing = SeriesPositionResolver.Resolve(all, "Lantern Road", null, 3);

        Assert.AreEqual("The Lantern Road|Lanterns at Dusk|The Last Lantern", string.Join("|", listing.Books.Select(book => book.Title)));
    }

    [TestMethod]
    public void DifferentSeriesWithTheWordsAreAmbiguousUnlessOneNameIsExact()
    {
        var other = Book("Lantern Road Chronicles of Mist", 1, "Mist Book");

        var ambiguous = SeriesPositionResolver.Resolve(Lanterns.Append(other).Select(book => book.SeriesName == "The Lantern Road" ? book : other), "lantern", 1, 5);
        var exact = SeriesPositionResolver.Resolve(Lanterns.Append(other), "Lantern Road", 1, 5);

        Assert.AreEqual(SeriesLookupKind.AmbiguousSeries, ambiguous.Kind);
        CollectionAssert.Contains(ambiguous.AmbiguousSeriesNames.ToArray(), "The Lantern Road");
        Assert.AreEqual(SeriesLookupKind.Found, exact.Kind, "The series whose name is exactly the request wins.");
        Assert.AreEqual("The Lantern Road", exact.Books.Single().Title);
    }

    [TestMethod]
    public void TheSameBookFromTwoSourcesIsOneBookButGenuinelyDifferentOnesAreBoth()
    {
        var twice = Lanterns.Append(Book("The Lantern Road", 2, "Lanterns at Dusk", "Mara Quill, Editor")).ToArray();
        var disagree = Lanterns.Append(Book("The Lantern Road", 2, "Lanterns at Dawn"));

        Assert.AreEqual(1, SeriesPositionResolver.Resolve(twice, "Lantern Road", 2, 5).Books.Count);
        Assert.AreEqual(2, SeriesPositionResolver.Resolve(disagree, "Lantern Road", 2, 5).Books.Count, "Providers disagree: the caller must ask.");
    }

    [TestMethod]
    public void UnknownSeriesAndEmptyNamesAreNotFound()
    {
        Assert.AreEqual(SeriesLookupKind.NotFound, SeriesPositionResolver.Resolve(Lanterns, "Murderbot", 1, 5).Kind);
        Assert.AreEqual(SeriesLookupKind.NotFound, SeriesPositionResolver.Resolve(Lanterns, "the", 1, 5).Kind, "Noise words alone match nothing.");
        Assert.AreEqual(SeriesLookupKind.NotFound, SeriesPositionResolver.Resolve([], "Lantern Road", 1, 5).Kind);
    }

    [TestMethod]
    [DataRow(null, "2", 2)]
    [DataRow(null, "2.5", 2.5)]
    [DataRow(null, "Book Two", null)]
    [DataRow(7, "ignored", 7)]
    public void PositionComesFromTheSortValueThenANumericLabel(object? sort, string label, object? expected)
    {
        var result = SeriesPositionResolver.PositionOf(sort is null ? null : Convert.ToDecimal(sort, System.Globalization.CultureInfo.InvariantCulture), label);

        Assert.AreEqual(expected is null ? null : Convert.ToDecimal(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    // ---- through the conversation -------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheThirdBookOfASeriesIsAskedOfTheSharedAnswerAndRequestedThroughTheOrdinaryFlow()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Position(3, TestAnswer.Entry("The Last Lantern", 3));

        var asked = await harness.SayAsync("the third Lantern Road book");

        // The wording is routed to the shared contract, not to a chat-only series approximation.
        var request = harness.Answers.Requests.Single();
        Assert.AreEqual(CatalogAnswerOperation.Position, request.Operation);
        Assert.AreEqual(3m, request.Position);
        Assert.AreEqual("Lantern Road", request.Text);
        Assert.IsNull(request.ContextRootId, "A new question never reuses an earlier retained answer.");

        Assert.AreEqual(ConversationReplyKind.Answer, asked.Reply.Kind);
        StringAssert.Contains(asked.Reply.Text, "Book 3 of The Lantern Road is");
        StringAssert.Contains(asked.Reply.Text, "The Last Lantern");

        var format = await harness.SayAsync("1");
        var created = await harness.SayAsync("ebook");

        Assert.AreEqual(ConversationReplyKind.AskFormat, format.Reply.Kind);
        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.AreEqual(harness.Tools.WorkIds[TestAnswer.Work("The Last Lantern").ExternalId], harness.Tools.Created.Single().WorkId);
    }

    [TestMethod]
    public async Task ARequestWordedWithAFormatSkipsToTheRequest()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Position(3, TestAnswer.Entry("The Last Lantern", 3));

        var created = await harness.SayAsync("get the third Lantern Road book audiobook");

        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.AreEqual("Audiobook", string.Join(",", harness.Tools.Created.Single().Formats));
    }

    [TestMethod]
    public async Task ARequestWithoutAFormatAsksForOneBeforeCreatingAnything()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Position(3, TestAnswer.Entry("The Last Lantern", 3));

        var asked = await harness.SayAsync("request the third Lantern Road book");

        Assert.AreEqual(ConversationReplyKind.AskFormat, asked.Reply.Kind);
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task ARecordAlreadyInTheCatalogIsRequestedByItsSavedIdWithoutResolvingItAgain()
    {
        var harness = new ConversationHarness();
        var saved = Guid.NewGuid();
        harness.Answers.Script = _ => TestAnswer.Position(3, new CatalogAnswerEntry
        {
            SavedWorkId = saved,
            Title = "The Last Lantern",
            Publication = TestAnswer.Entry("x").Publication
        });

        var created = await harness.SayAsync("get the third Lantern Road book audiobook");

        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.AreEqual(saved, harness.Tools.Created.Single().WorkId);
        Assert.IsFalse(harness.Tools.Calls.Any(call => call.StartsWith("resolve:", StringComparison.Ordinal)),
            "A saved record is never sent through the catalog write resolver just to obtain an id.");
    }

    [TestMethod]
    public async Task TwoBooksAtOnePositionAreBothShownAndNeverPickedForThePerson()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Position(2, TestAnswer.Entry("Lanterns at Dusk", 2), TestAnswer.Entry("Lanterns at Dawn", 2));

        var asked = await harness.SayAsync("get the second Lantern Road book audiobook");

        Assert.AreEqual(ConversationReplyKind.Answer, asked.Reply.Kind);
        Assert.HasCount(2, asked.Reply.Choices);
        StringAssert.Contains(asked.Reply.Text, "More than one book states book 2");
        Assert.IsEmpty(harness.Tools.Created, "Ambiguity means ask.");
    }

    [TestMethod]
    public async Task AMissingPositionAndAnUnknownSeriesNeverGuessAndFallBackToALabelledSearch()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = request => request.Operation == CatalogAnswerOperation.Position
            ? TestAnswer.Nothing(request.Operation, CatalogAnswerCodes.PositionNotInCheckedEntries)
            : TestAnswer.Nothing(request.Operation, CatalogAnswerCodes.NoChoicesFound);
        harness.Tools.Results["Samuel"] = new ConversationSearchResult([FakeTools.Choice("s1", "The First Book of Samuel")], false, []);

        var missing = await harness.SayAsync("the fifth Lantern Road book");
        var unknown = await harness.SayAsync("reading order of Samuel");

        StringAssert.Contains(missing.Reply.Text, "No book I checked claims book 5 of The Lantern Road");
        StringAssert.Contains(unknown.Reply.Text, "I couldn't confirm which books are in “Samuel”");
        Assert.AreEqual(ConversationReplyKind.Choices, unknown.Reply.Kind, "The fallback is an ordinary search, labelled as unconfirmed.");
        CollectionAssert.Contains(harness.Tools.Calls, "search:Samuel");
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task AListingAsksForTheSeriesMembersAndSaysTheListIsNotComplete()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Members(
            TestAnswer.Entry("The Lantern Road", 1), TestAnswer.Entry("Lanterns at Dusk", 2), TestAnswer.Entry("The Last Lantern", 3));

        var listing = await harness.SayAsync("reading order of Lantern Road");

        Assert.AreEqual(CatalogAnswerOperation.SeriesMembers, harness.Answers.Requests.Single().Operation);
        Assert.AreEqual(ConversationReplyKind.Answer, listing.Reply.Kind);
        StringAssert.Contains(listing.Reply.Text, "in order");
        StringAssert.Contains(listing.Reply.Text, "not a complete list");
        var lines = listing.Reply.Answers!.Single().Lines;
        Assert.HasCount(3, lines);
        StringAssert.Contains(lines[1].Title, "Lanterns at Dusk");
        StringAssert.Contains(lines[2].Title, "The Last Lantern");
    }

    [TestMethod]
    public async Task ASeriesNameIsOfferedAsAChoiceAndTheChosenOneIsTheRetainedReference()
    {
        var harness = new ConversationHarness();
        harness.Answers.Script = _ => TestAnswer.Offering(TestAnswer.SeriesChoice());

        var offered = await harness.SayAsync("the latest Lantern Road book");

        Assert.AreEqual(ConversationReplyKind.Clarification, offered.Reply.Kind);
        Assert.HasCount(1, offered.Reply.Choices);

        harness.Answers.Script = _ => TestAnswer.Latest(TestAnswer.Entry("Road's End", 4));
        var answered = await harness.SayAsync("1");

        var followUp = harness.Answers.Requests[^1];
        Assert.AreEqual(CatalogAnswerOperation.LatestPublished, followUp.Operation);
        Assert.AreEqual(CatalogSubjectKind.Series, followUp.Subject!.Kind);
        Assert.AreEqual(TestAnswer.Series, followUp.Subject.Native);
        Assert.IsNotNull(followUp.ContextRootId, "The selection continues on the root the choice was retained under.");
        Assert.AreEqual(ConversationReplyKind.Answer, answered.Reply.Kind);
        StringAssert.Contains(answered.Reply.Text, "The latest published book I can find in The Lantern Road is");
    }

    [TestMethod]
    public async Task TheBookAfterANamedBookIsOfferedAndTheLatestOneSaysSo()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = new SeriesLookupResult(SeriesLookupKind.Found, "The Lantern Road", [Lanterns[2]], [], [3m]);

        var next = await harness.SayAsync("what is the next book in Lantern Road after Lanterns at Dusk");

        Assert.AreEqual(ConversationReplyKind.Choices, next.Reply.Kind);
        StringAssert.Contains(next.Reply.Text, "After Lanterns at Dusk, the next book in The Lantern Road is");
        StringAssert.Contains(next.Reply.Text, "The Last Lantern");
        CollectionAssert.Contains(harness.Tools.Calls, "series:Lantern Road|all|after:Lanterns at Dusk");

        harness.Tools.SeriesResults["Lantern Road"] = new SeriesLookupResult(SeriesLookupKind.PositionMissing, "The Lantern Road", [], [], [1m, 2m, 3m, 4m]);
        var last = await harness.SayAsync("what is the next book in Lantern Road after Road's End");

        Assert.AreEqual(ConversationReplyKind.Text, last.Reply.Kind);
        StringAssert.Contains(last.Reply.Text, "I don't know of a book after");
    }
}
