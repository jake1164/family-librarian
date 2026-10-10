using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Infrastructure.Gutenberg;

namespace FamilyLibrarian.Infrastructure.Tests.Gutenberg;

[TestClass]
public sealed class GutenbergNarrationParserTests
{
    [TestMethod]
    public void ASubtitleReadingByCreditIsClassifiedAsHumanWithTheNarratorsName()
    {
        // Real fixture: Project Gutenberg #28794's readme.
        const string readme = """
            The Project Gutenberg EBook Moby Dick by Herman Melville

            Title: Moby Dick

            Subtitle: Reading by Stewart Wills

            Author: Herman Melville
            """;

        var evidence = GutenbergNarrationParser.Parse(readme);

        Assert.AreEqual(NarrationKind.Human, evidence.Kind);
        Assert.AreEqual("Stewart Wills", evidence.Narrator);
    }

    [TestMethod]
    public void AReadByCreditWithTheNameOnAFollowingLineIsClassifiedAsHuman()
    {
        // Real fixture: the same readme also states the credit this way,
        // further down, with a blank line between the sentence and the name.
        const string readme = """
            This audio reading of Moby Dick is read by

            Stewart Wills

            Contents
            """;

        var evidence = GutenbergNarrationParser.Parse(readme);

        Assert.AreEqual(NarrationKind.Human, evidence.Kind);
        Assert.AreEqual("Stewart Wills", evidence.Narrator);
    }

    [TestMethod]
    public void ANarratedByCreditIsClassifiedAsHuman()
    {
        var evidence = GutenbergNarrationParser.Parse("This recording is narrated by Jane Example.");

        Assert.AreEqual(NarrationKind.Human, evidence.Kind);
        Assert.AreEqual("Jane Example", evidence.Narrator);
    }

    [TestMethod]
    [DataRow("This is a computer generated audiobook.")]
    [DataRow("Produced using text-to-speech software.")]
    [DataRow("Narration by speech synthesis.")]
    public void AnExplicitSyntheticStatementIsClassifiedAsSynthetic(string readme)
    {
        var evidence = GutenbergNarrationParser.Parse(readme);

        Assert.AreEqual(NarrationKind.Synthetic, evidence.Kind);
        Assert.IsNull(evidence.Narrator);
    }

    [TestMethod]
    public void InsufficientEvidenceIsClassifiedAsUnknown()
    {
        // Real fixture: Project Gutenberg #9147's readme states who produced
        // the recording, but never says whether that means a human reading
        // or a computer-generated one -- this must not be guessed either way.
        const string readme = """
            Produced by Mike Eschman

            Audio performance Copyright (C) 2003 by Mike Eschman.

            This is an audio eBook created by Mike Eschman.
            """;

        var evidence = GutenbergNarrationParser.Parse(readme);

        Assert.AreEqual(NarrationKind.Unknown, evidence.Kind);
        Assert.IsNull(evidence.Narrator);
    }

    [TestMethod]
    public void EmptyOrMissingTextIsClassifiedAsUnknown()
    {
        Assert.AreEqual(NarrationKind.Unknown, GutenbergNarrationParser.Parse(null).Kind);
        Assert.AreEqual(NarrationKind.Unknown, GutenbergNarrationParser.Parse("").Kind);
        Assert.AreEqual(NarrationKind.Unknown, GutenbergNarrationParser.Parse("   ").Kind);
    }

    [TestMethod]
    public void AReadByPhraseFollowedByUnrelatedContentDoesNotFabricateAName()
    {
        const string readme = """
            This audio reading is read by

            Chapter 1: The Beginning
            """;

        var evidence = GutenbergNarrationParser.Parse(readme);

        Assert.AreEqual(NarrationKind.Unknown, evidence.Kind);
    }
}
