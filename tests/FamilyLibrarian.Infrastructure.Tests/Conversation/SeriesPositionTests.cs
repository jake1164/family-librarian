using FamilyLibrarian.Application.Catalog;
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

    private static SeriesLookupResult Found(params SeriesBook[] books) =>
        new(SeriesLookupKind.Found, "The Lantern Road", books, [], books.Select(book => book.Position!.Value).ToArray());

    [TestMethod]
    public async Task TheThirdBookOfASeriesIsOfferedAndRequestedThroughTheOrdinaryFlow()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found(Lanterns[2]);

        var asked = await harness.SayAsync("the third Lantern Road book");
        var format = await harness.SayAsync("1");
        var created = await harness.SayAsync("ebook");

        Assert.AreEqual(ConversationReplyKind.Choices, asked.Reply.Kind);
        StringAssert.Contains(asked.Reply.Text, "book 3 is");
        StringAssert.Contains(asked.Reply.Text, "The Last Lantern");
        Assert.AreEqual(ConversationReplyKind.AskFormat, format.Reply.Kind);
        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.AreEqual(harness.Tools.WorkIds[Lanterns[2].ExternalId], harness.Tools.Created.Single().WorkId);
    }

    [TestMethod]
    public async Task ARequestWordedWithAFormatSkipsToTheRequest()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found(Lanterns[2]);

        var created = await harness.SayAsync("get the third Lantern Road book audiobook");

        Assert.AreEqual(ConversationReplyKind.RequestCreated, created.Reply.Kind);
        Assert.AreEqual("Audiobook", string.Join(",", harness.Tools.Created.Single().Formats));
    }

    [TestMethod]
    public async Task ARequestWithoutAFormatAsksForOneBeforeCreatingAnything()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found(Lanterns[2]);

        var asked = await harness.SayAsync("request the third Lantern Road book");

        Assert.AreEqual(ConversationReplyKind.AskFormat, asked.Reply.Kind);
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task ProvidersDisagreeingAtAPositionAreOfferedAsChoicesNeverPickedForTheUser()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found(Book("The Lantern Road", 2, "Lanterns at Dusk"), Book("The Lantern Road", 2, "Lanterns at Dawn"));

        var asked = await harness.SayAsync("get the second Lantern Road book audiobook");

        Assert.AreEqual(ConversationReplyKind.Choices, asked.Reply.Kind);
        Assert.HasCount(2, asked.Reply.Choices);
        StringAssert.Contains(asked.Reply.Text, "More than one book");
        Assert.IsEmpty(harness.Tools.Created, "Ambiguity means ask.");
    }

    [TestMethod]
    public async Task AMissingPositionAmbiguousSeriesAndAnUnknownSeriesAreHandledWithoutGuessing()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = new SeriesLookupResult(SeriesLookupKind.PositionMissing, "The Lantern Road", [], [], [1m, 2m]);
        harness.Tools.SeriesResults["Dune"] = new SeriesLookupResult(SeriesLookupKind.AmbiguousSeries, null, [], ["Dune", "Dune: Legends"], []);
        harness.Tools.Results["third Samuel book"] = new ConversationSearchResult([], false, []);

        var missing = await harness.SayAsync("the fifth Lantern Road book");
        var ambiguous = await harness.SayAsync("book 2 of Dune");
        var unknown = await harness.SayAsync("the third Samuel book");

        StringAssert.Contains(missing.Reply.Text, "I know of 1, 2");
        StringAssert.Contains(ambiguous.Reply.Text, "More than one series");
        Assert.AreEqual(ConversationReplyKind.Text, unknown.Reply.Kind, "No such series: it was searched as ordinary words.");
        CollectionAssert.Contains(harness.Tools.Calls, "search:the third Samuel book");
        Assert.IsEmpty(harness.Tools.Created);
    }

    [TestMethod]
    public async Task AListingOffersTheBooksInOrder()
    {
        var harness = new ConversationHarness();
        harness.Tools.SeriesResults["Lantern Road"] = Found(Lanterns[0], Lanterns[1], Lanterns[2]);

        var listing = await harness.SayAsync("reading order of Lantern Road");

        StringAssert.Contains(listing.Reply.Text, "in order");
        Assert.AreEqual("The Lantern Road|Lanterns at Dusk|The Last Lantern", string.Join("|", listing.Reply.Choices.Select(choice => choice.Title)));
    }
}
