using FamilyLibrarian.Application.Matching;

namespace FamilyLibrarian.Infrastructure.Tests.Matching;

[TestClass]
public sealed class LanguageFunctionWordsTests
{
    [TestMethod]
    [DataRow("Der ehemalige Navy SEAL kehrt nach einem Hinterhalt zurück, der sein Team das Leben kostete. Er will wissen, wer ihn verraten hat und warum es dazu kam. Das ist die Geschichte von einem Mann, der nicht aufgibt und mit allen Mitteln für die Wahrheit kämpft.", "de")]
    [DataRow("The story of a man who has been betrayed by the people that he trusted, and of the long road that he must take to find out what happened to his team, and who was behind it, and why they did it to him.", "en")]
    [DataRow("Le héros est un homme qui ne veut pas partir, mais il est obligé de le faire pour sauver sa famille, et il sait que les autres ont peur de lui, parce que son passé est dans la ville avec ses amis et avec elle.", "fr")]
    [DataRow("El libro cuenta la historia de un hombre que se va de su casa para buscar a su hermano, pero también sobre los problemas que tiene con su familia, y sin ayuda esta vez no sabe cuando va a volver, ya que hay muy pocos que lo ayudan.", "es")]
    public void AClearlyWrittenTextIsRecognized(string text, string expected)
    {
        Assert.IsTrue(LanguageFunctionWords.TryInfer(text, out var language, out var evidence));
        Assert.AreEqual(expected, language);
        Assert.IsGreaterThanOrEqualTo(LanguageFunctionWords.MinimumEvidence, evidence);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Thriller")]
    [DataRow("Ein kurzer Satz mit der und das.")]
    [DataRow("Chapter 01 - Jack Carr - The Terminal List")]
    public void ShortOrEmptyTextIsUnknown(string? text)
    {
        Assert.IsFalse(LanguageFunctionWords.TryInfer(text, out _, out _));
    }

    [TestMethod]
    public void AMixedTextWithoutADominantLanguageIsUnknown()
    {
        const string mixed = "The man and the dog went to the park with his hat and it was not the one that she had. " +
            "Der Mann und das Kind sind nicht da, er hat sie mit dem Hund gesehen und von dort aus nach Hause.";

        Assert.IsFalse(LanguageFunctionWords.TryInfer(mixed, out _, out _));
    }

    [TestMethod]
    public void TextBeyondTheBoundIsNotRead()
    {
        var padding = new string(' ', LanguageFunctionWords.MaxTextLength);
        var english = string.Concat(Enumerable.Repeat("the and of to is that it for with as his her ", 2));

        Assert.IsFalse(LanguageFunctionWords.TryInfer(padding + english, out _, out _));
    }
}
