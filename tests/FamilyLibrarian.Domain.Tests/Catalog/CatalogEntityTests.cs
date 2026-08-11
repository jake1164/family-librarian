using FamilyLibrarian.Domain.Catalog;

namespace FamilyLibrarian.Domain.Tests.Catalog;

[TestClass]
public sealed class CatalogEntityTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NormalizeForMatchRemovesDiacriticsAndCollapsesPunctuation()
    {
        var normalized = CatalogText.NormalizeForMatch("  L'Été—of   Mÿ Life!  ");

        Assert.AreEqual("l ete of my life", normalized);
    }

    [TestMethod]
    public void WorkRejectsEditionFromAnotherWork()
    {
        var work = CreateWork("Project Hail Mary");
        var unrelatedWork = CreateWork("The Martian");
        var edition = new Edition(
            unrelatedWork.Id,
            "The Martian",
            EditionFormat.Ebook,
            "9780553418026",
            new DateOnly(2014, 2, 11),
            CreatedAtUtc);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => work.AddEdition(edition));

        StringAssert.Contains(exception.Message, "different Work");
    }

    [TestMethod]
    public void WorkDoesNotDuplicateAnEditionWithTheSameIsbn()
    {
        var work = CreateWork("Project Hail Mary");
        work.AddEdition(new Edition(
            work.Id,
            "Project Hail Mary",
            EditionFormat.Ebook,
            "9780593135204",
            new DateOnly(2021, 5, 4),
            CreatedAtUtc));
        work.AddEdition(new Edition(
            work.Id,
            "Project Hail Mary: duplicate evidence",
            EditionFormat.Unknown,
            "9780593135204",
            null,
            CreatedAtUtc));

        Assert.HasCount(1, work.Editions);
    }

    [TestMethod]
    public void WorkAuthorRejectsNegativeOrdinal()
    {
        var work = CreateWork("Project Hail Mary");
        var author = new Author("Andy Weir", null, CreatedAtUtc);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => work.AddAuthor(author, -1));
    }

    private static Work CreateWork(string title) => new(
        title,
        null,
        null,
        null,
        PublicationStatus.Unknown,
        CreatedAtUtc);
}
