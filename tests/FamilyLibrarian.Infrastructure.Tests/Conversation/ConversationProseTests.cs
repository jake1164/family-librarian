using FamilyLibrarian.Application.Conversation;

namespace FamilyLibrarian.Infrastructure.Tests.Conversation;

/// <summary>The deterministic gate that decides whether a search message is a sentence worth an optional interpretation.</summary>
[TestClass]
public sealed class ConversationProseTests
{
    [TestMethod]
    [DataRow("which novel is the one with the russian submarine captain?")]
    [DataRow("what book is after sum of all fears by clancy")]
    [DataRow("no, talking tom clancy jack ryan series")]
    [DataRow("i want that sad book about a lighthouse")]
    [DataRow("sum of all fears tom clancy paperback edition please")]
    [DataRow("is there a sequel to dune")]
    public void SentencesAreProse(string text) => Assert.IsTrue(ConversationProse.LooksLikeProse(text), text);

    [TestMethod]
    [DataRow("dune")]
    [DataRow("dune frank herbert")]
    [DataRow("the sum of all fears")]
    [DataRow("all systems red")]
    [DataRow("")]
    public void TitlesAndAuthorsAreNot(string text) => Assert.IsFalse(ConversationProse.LooksLikeProse(text), text);

    [TestMethod]
    public void ATitleResultThatIsTheWholeMessageCountsRegardlessOfCaseAndPunctuation()
    {
        ConversationChoiceRef[] choices =
        [
            new("fake", "1", "Sum of All Fears: A Novel", "Tom Clancy", null),
            new("fake", "2", "The Sum of All Fears", "Tom Clancy", null)
        ];

        Assert.IsTrue(ConversationProse.HasExactTitle(choices, "the sum of all fears"));
        Assert.IsTrue(ConversationProse.HasExactTitle(choices, "  THE SUM-OF-ALL FEARS! "));
        Assert.IsFalse(ConversationProse.HasExactTitle(choices, "what book is next after the sum of all fears"));
        Assert.IsFalse(ConversationProse.HasExactTitle(choices, ""));
    }
}
