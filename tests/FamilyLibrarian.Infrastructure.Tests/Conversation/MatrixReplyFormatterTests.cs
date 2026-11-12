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
}
