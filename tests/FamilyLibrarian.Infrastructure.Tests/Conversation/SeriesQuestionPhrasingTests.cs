using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Conversation;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>
/// A6-CHAT-SERIES: phrasings seen in live Matrix use ("the new book in the Fourth Wing series", "list all the Jack Ryan books in
/// order", "what is the next book in Jack Ryan after Debt of Honor") and entries that are not whole numbers or have no number.
/// </summary>
[TestClass]
public sealed class SeriesQuestionPhrasingTests
{
    private static SeriesBook Book(string series, decimal? position, string title) =>
        new(series, position, title, "A. Author", "demo", title.Replace(' ', '-').ToLowerInvariant());

    private static readonly SeriesBook[] FourthWing =
    [
        Book("The Empyrean", 1, "Fourth Wing"),
        Book("The Empyrean", 2, "Iron Flame"),
        Book("The Empyrean", 3, "Onyx Storm"),
        Book("The Empyrean", 3.5m, "Threshing Day")
    ];

    private static readonly string[] ListedInOrder = ["Fourth Wing", "Iron Flame", "Onyx Storm", "Fourth Wing Companion Novella"];

    private static readonly SeriesBook[] JackRyan =
    [
        Book("Jack Ryan", 5, "Clear and Present Danger"),
        Book("Jack Ryan", 6, "The Sum of All Fears"),
        Book("Jack Ryan", 7, "Debt of Honor"),
        Book("Jack Ryan", 8, "Executive Orders"),
        Book("Jack Ryan", 9, "Rainbow Six")
    ];

    [TestMethod]
    [DataRow("the new book in the Fourth Wing series")]
    [DataRow("what is the newest Fourth Wing book")]
    [DataRow("is there a new Fourth Wing book")]
    public void TheNewBookMeansTheLatestPosition(string text)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var position, out var after), text);
        Assert.AreEqual("Fourth Wing", name);
        Assert.AreEqual(SeriesPositionResolver.LastPosition, position);
        Assert.IsNull(after);
    }

    [TestMethod]
    [DataRow("list all the jack ryan books in order")]
    [DataRow("list the Jack Ryan books in order")]
    [DataRow("show me all of the Jack Ryan books")]
    [DataRow("all the jack ryan books")]
    public void AListingRequestIgnoresFillerWords(string text)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var position, out _), text);
        Assert.AreEqual("jack ryan", name, ignoreCase: true);
        Assert.IsNull(position);
    }

    [TestMethod]
    [DataRow("what is the next book in jack ryan after debt of honor", "jack ryan", "debt of honor")]
    [DataRow("what's the next Jack Ryan book after Debt of Honor?", "Jack Ryan", "Debt of Honor")]
    [DataRow("what comes after Debt of Honor in the Jack Ryan series", "Jack Ryan", "Debt of Honor")]
    [DataRow("what book is next after Debt of Honor in Jack Ryan", "Jack Ryan", "Debt of Honor")]
    public void NextAfterATitleNamesTheSeriesAndTheTitle(string text, string series, string title)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var position, out var after), text);
        Assert.AreEqual(series, name, ignoreCase: true);
        Assert.AreEqual(title, after, ignoreCase: true);
        Assert.IsNull(position);
    }

    [TestMethod]
    public void NextAfterATitleWithoutASeriesLeavesTheSeriesToBeFound()
    {
        Assert.IsTrue(SeriesParser.TryParse("what book is next after Debt of Honor", out var name, out _, out var after));
        Assert.AreEqual(string.Empty, name);
        Assert.AreEqual("Debt of Honor", after, ignoreCase: true);
    }

    [TestMethod]
    [DataRow("book 3.5 of Fourth Wing", "Fourth Wing", 3.5)]
    [DataRow("Fourth Wing book 2.5", "Fourth Wing", 2.5)]
    public void DecimalPositionsAreRecognized(string text, string series, double position)
    {
        Assert.IsTrue(SeriesParser.TryParse(text, out var name, out var parsed, out _), text);
        Assert.AreEqual(series, name);
        Assert.AreEqual((decimal)position, parsed);
    }

    [TestMethod]
    [DataRow("The Lantern Road")]
    [DataRow("After the Fall")]
    [DataRow("Next to Normal")]
    [DataRow("What Comes After")]
    public void OrdinaryTitlesStillAreNotSeriesQuestions(string text) =>
        Assert.IsFalse(SeriesParser.TryParse(text, out _, out _, out _), text);

    [TestMethod]
    public void TheNextBookIsTheNextHigherPositionIncludingHalfSteps()
    {
        var result = SeriesPositionResolver.ResolveAfter(JackRyan, "Jack Ryan", "Debt of Honor", 5);

        Assert.AreEqual(SeriesLookupKind.Found, result.Kind);
        Assert.AreEqual("Executive Orders", result.Books.Single().Title);

        var wing = SeriesPositionResolver.ResolveAfter(FourthWing, "Fourth Wing", "Onyx Storm", 5);
        Assert.AreEqual("Threshing Day", wing.Books.Single().Title);
    }

    [TestMethod]
    public void TheTitleIsMatchedLooselyButNotByAGuess()
    {
        Assert.AreEqual("Executive Orders", SeriesPositionResolver.ResolveAfter(JackRyan, null, "debt of honor", 5).Books.Single().Title);
        Assert.AreEqual(SeriesLookupKind.NotFound, SeriesPositionResolver.ResolveAfter(JackRyan, "Jack Ryan", "Dead Poets Society", 5).Kind);
    }

    [TestMethod]
    public void AfterTheLatestBookTheAnswerSaysThereIsNothingKnownAfterIt()
    {
        var result = SeriesPositionResolver.ResolveAfter(JackRyan, "Jack Ryan", "Rainbow Six", 5);

        Assert.AreEqual(SeriesLookupKind.PositionMissing, result.Kind);
        Assert.AreEqual("Jack Ryan", result.SeriesName);
    }

    [TestMethod]
    public void AListingKeepsNumberedBooksInOrderThenShowsUnnumberedCompanions()
    {
        SeriesBook[] books = [.. FourthWing.Take(3), Book("The Empyrean", null, "Fourth Wing Companion Novella")];

        var result = SeriesPositionResolver.Resolve(books, "Empyrean", null, 10);

        CollectionAssert.AreEqual(ListedInOrder, result.Books.Select(book => book.Title).ToArray());
    }

    [TestMethod]
    public void ASeriesNamedAfterItsFirstBookIsFoundThroughThatBook()
    {
        var result = SeriesPositionResolver.Resolve(FourthWing, "Fourth Wing", SeriesPositionResolver.LastPosition, 5);

        Assert.AreEqual(SeriesLookupKind.Found, result.Kind);
        Assert.AreEqual("The Empyrean", result.SeriesName);
        Assert.AreEqual("Threshing Day", result.Books.Single().Title);
    }

    [TestMethod]
    public void TheLatestBookIsTheHighestNumberEvenWhenItIsAHalfStep()
    {
        var result = SeriesPositionResolver.Resolve(FourthWing, "Empyrean", SeriesPositionResolver.LastPosition, 5);

        Assert.AreEqual("Threshing Day", result.Books.Single().Title);
    }
}
