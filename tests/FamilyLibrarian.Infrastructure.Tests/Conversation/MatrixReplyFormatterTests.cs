using FamilyLibrarian.Application.Communications;
using FamilyLibrarian.Application.Conversation;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

[TestClass]
public sealed class MatrixReplyFormatterTests
{
    private static ConversationReply Reply(string text) => ConversationReply.Of(ConversationReplyKind.Text, text);

    [TestMethod]
    public void ShortRepliesPassThroughAndKeepTheirLineBreaks() =>
        Assert.AreEqual("I found these:\n1. The Lantern Road", MatrixReplyFormatter.ToText(Reply("I found these:\n1. The Lantern Road")));

    [TestMethod]
    public void ControlCharactersAreRemovedAndMarkupIsLeftAsInertText()
    {
        var text = MatrixReplyFormatter.ToText(Reply("Title\u0007 <b>bold</b> [x](http://example.test)\r"));

        Assert.AreEqual("Title <b>bold</b> [x](http://example.test)", text, "Plain text is sent; the client does not render it as formatting from us.");
    }

    [TestMethod]
    public void LongRepliesAreCutAtALineBreakAndMarkedNeverSplittingAnEntry()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 200).Select(index => $"{index}. {new string('x', 30)}"));

        var text = MatrixReplyFormatter.ToText(Reply(lines));

        Assert.IsLessThanOrEqualTo(MatrixReplyFormatter.MaxLength + 2, text.Length);
        StringAssert.EndsWith(text, "\n…");
        Assert.IsTrue(text.Split('\n')[^2].EndsWith(new string('x', 30), StringComparison.Ordinal), "The last entry is whole.");
    }

    [TestMethod]
    public void AnEmptyReplyBecomesAHelpPointer() =>
        StringAssert.Contains(MatrixReplyFormatter.ToText(Reply("  \u0007 ")), "help");

    private const string Origin = "https://fl.example.test";
    private static readonly Guid WorkId = Guid.Parse("0f1e2d3c-4b5a-4968-8776-655443322110");
    private static readonly Guid SeriesId = Guid.Parse("11223344-5566-4778-8899-aabbccddeeff");

    private static ConversationReply Answered(string text, string? cardHref, params string?[] lineHrefs) =>
        new(ConversationReplyKind.Answer, text, [], [], [],
            Answers: [new ConversationAnswerCard("answer", "Heading",
                lineHrefs.Select((href, index) => new ConversationAnswerCardLine($"Book {index + 1}", null, [], href)).ToArray(), [], [], cardHref)]);

    [TestMethod]
    public void ASavedRecordGetsOneLinkOnTheAppOriginAfterTheUnchangedText()
    {
        var text = MatrixReplyFormatter.ToText(Answered("Book 1 is:\n1. Lanterns at Dawn", null, $"/works/{WorkId:D}", null), Origin);

        Assert.AreEqual($"Book 1 is:\n1. Lanterns at Dawn\nOpen in Family Librarian: https://fl.example.test/works/{WorkId:D}", text);
    }

    [TestMethod]
    public void TheAnswersOwnSavedSeriesIsPreferredOverAListedBook()
    {
        var link = MatrixReplyFormatter.CatalogLink(Answered("x", $"/series/{SeriesId:D}", $"/works/{WorkId:D}"), Origin);

        Assert.AreEqual($"https://fl.example.test/series/{SeriesId:D}", link);
    }

    [TestMethod]
    public void WithoutAnOriginTheTextIsExactlyWhatItWasBefore()
    {
        var reply = Answered("Book 1 is:\n1. Lanterns at Dawn", null, $"/works/{WorkId:D}");

        Assert.AreEqual(MatrixReplyFormatter.ToText(reply), MatrixReplyFormatter.ToText(reply, null));
        Assert.AreEqual("Book 1 is:\n1. Lanterns at Dawn", MatrixReplyFormatter.ToText(reply, "  "));
    }

    [TestMethod]
    [DataRow("https://source.example/works/0f1e2d3c-4b5a-4968-8776-655443322110")]
    [DataRow("//source.example/works/0f1e2d3c-4b5a-4968-8776-655443322110")]
    [DataRow("/works/not-a-guid")]
    [DataRow("/works/0F1E2D3C-4B5A-4968-8776-655443322110")]
    [DataRow("/works/00000000-0000-0000-0000-000000000000")]
    [DataRow("/works/0f1e2d3c-4b5a-4968-8776-655443322110?next=https://source.example")]
    [DataRow("/admin/accounts")]
    [DataRow("javascript:alert(1)")]
    public void OnlyAnApplicationRouteWithASavedIdBecomesALink(string href) =>
        Assert.IsNull(MatrixReplyFormatter.CatalogLink(Answered("x", href, href), Origin), href);

    [TestMethod]
    [DataRow("fl.example.test")]
    [DataRow("https://fl.example.test/app")]
    [DataRow("https://fl.example.test/?q=1")]
    [DataRow("https://user:pass@fl.example.test")]
    [DataRow("ftp://fl.example.test")]
    public void AnOriginThatIsNotABareWebOriginGivesNoLink(string origin) =>
        Assert.IsNull(MatrixReplyFormatter.CatalogLink(Answered("x", null, $"/works/{WorkId:D}"), origin), origin);

    [TestMethod]
    public void AnAnswerThatFillsTheLimitIsSentWholeAndWithoutTheOptionalLink()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 200).Select(index => $"{index}. {new string('x', 30)}"));
        var reply = Answered(lines, null, $"/works/{WorkId:D}");

        var withOrigin = MatrixReplyFormatter.ToText(reply, Origin);

        Assert.AreEqual(MatrixReplyFormatter.ToText(reply), withOrigin, "The link never changes how much of the answer is shown.");
        Assert.DoesNotContain("Open in Family Librarian", withOrigin);
        StringAssert.EndsWith(withOrigin, "\n…");
    }

    [TestMethod]
    [DataRow("https://fl.example.test")]
    [DataRow("long")]
    public void TheScopeQualificationAtTheEndOfAFullAnswerIsNeverPushedOutByTheLink(string kind)
    {
        // An origin with a 247-character host: a valid bare origin that makes the link line very long.
        var longHost = string.Join('.', new string('a', 61), new string('b', 61), new string('c', 61), new string('d', 56), "test");
        Assert.AreEqual(247, longHost.Length);
        var origin = kind == "long" ? "https://" + longHost : Origin;
        const string tail = "\nThis is what I could check, not a complete list.\nReply with a number for details.";
        // The plain answer fits with only 40 characters to spare: too few for any link line.
        var body = new string('x', MatrixReplyFormatter.MaxLength - tail.Length - 40);
        var reply = Answered(body + tail, null, $"/works/{WorkId:D}");
        var plain = MatrixReplyFormatter.ToText(reply);
        Assert.AreEqual(MatrixReplyFormatter.MaxLength - 40, plain.Length);

        var text = MatrixReplyFormatter.ToText(reply, origin);

        Assert.AreEqual(plain, text, "The optional link is left out; nothing of the answer is.");
        StringAssert.EndsWith(text, "Reply with a number for details.");
        StringAssert.Contains(text, "not a complete list.");
    }

    [TestMethod]
    public void TheLongOriginLinkIsStillAddedWhenTheAnswerIsShortEnough()
    {
        var longHost = string.Join('.', new string('a', 61), new string('b', 61), new string('c', 61), new string('d', 56), "test");

        var text = MatrixReplyFormatter.ToText(Answered("Short answer.", null, $"/works/{WorkId:D}"), "https://" + longHost);

        Assert.AreEqual($"Short answer.\nOpen in Family Librarian: https://{longHost}/works/{WorkId:D}", text);
        Assert.IsLessThanOrEqualTo(MatrixReplyFormatter.MaxLength, text.Length);
    }

    [TestMethod]
    public void TheLinkIsAppendedWhenTheWholeAnswerAndTheLinkFit()
    {
        var body = new string('x', MatrixReplyFormatter.MaxLength - 200);

        var text = MatrixReplyFormatter.ToText(Answered(body, null, $"/works/{WorkId:D}"), Origin);

        Assert.AreEqual(body + $"\nOpen in Family Librarian: https://fl.example.test/works/{WorkId:D}", text);
    }

    [TestMethod]
    public void AnAuthorsRouteIsNotACatalogLinkSoAListedBookIsUsedInstead()
    {
        var authorId = Guid.Parse("a1111111-1111-4111-8111-111111111111");

        var link = MatrixReplyFormatter.CatalogLink(Answered("x", $"/authors/{authorId:D}", $"/works/{WorkId:D}"), Origin);

        Assert.AreEqual($"https://fl.example.test/works/{WorkId:D}", link);
    }
}
