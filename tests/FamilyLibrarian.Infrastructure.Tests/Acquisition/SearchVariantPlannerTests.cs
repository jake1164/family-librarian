using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Application.Providers;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class SearchVariantPlannerTests
{
    private static void AssertValues(IEnumerable<string> actual, params string[] expected) =>
        CollectionAssert.AreEqual(expected, actual.ToArray());

    private static BookIdentity IronFlame(params string[] isbns) => new(
        "Iron Flame", "Rebecca Yarros", isbns,
        Authors: [new BookAuthor("Rebecca Yarros", "author")],
        Series: [new BookSeries("The Empyrean", "2")],
        Language: "en", PublicationYear: 2023, Publisher: "Example Press",
        AlternateTitles: ["Iron Flame (Collector's Edition)", "Flamme de fer"]);

    [TestMethod]
    public void VariantsAreOrderedPreciseFirstAndChangeOneDimension()
    {
        var plan = SearchVariantPlanner.Plan(IronFlame("9780000000011", "9780000000028"), RequestMediaType.Ebook);

        CollectionAssert.AreEqual(
            new[]
            {
                (SearchVariantKind.Base, ""),
                (SearchVariantKind.Isbn, "9780000000028"),
                (SearchVariantKind.AlternateTitle, "FLAMME DE FER"),
                (SearchVariantKind.SeriesPosition, "THE EMPYREAN#2"),
                (SearchVariantKind.AuthorVariant, "YARROS, REBECCA"),
                (SearchVariantKind.AuthorVariant, "R. YARROS"),
            },
            plan.Select(variant => (variant.Kind, variant.Key)).ToArray());
    }

    [TestMethod]
    public void TheBaseVariantIsExactlyTheRequestARecheckHasAlwaysSent()
    {
        var baseVariant = SearchVariantPlanner.Plan(IronFlame("9780000000011", "9780000000028"), RequestMediaType.Ebook)[0];

        Assert.AreEqual("Iron Flame", baseVariant.Work.Title);
        Assert.AreEqual("Rebecca Yarros", baseVariant.Work.Authors.Single().Name);
        Assert.AreEqual("The Empyrean", baseVariant.Work.Series.Single().Name);
        Assert.AreEqual("9780000000011", baseVariant.Edition.Identifiers.Single().Value);
        Assert.AreEqual("en", baseVariant.Edition.Language);
    }

    [TestMethod]
    public void EachVariantChangesExactlyOneDimension()
    {
        var plan = SearchVariantPlanner.Plan(IronFlame("9780000000011", "9780000000028"), RequestMediaType.Ebook);
        var baseVariant = plan[0];

        var isbn = plan.Single(variant => variant.Kind == SearchVariantKind.Isbn);
        Assert.AreEqual(baseVariant.Work, isbn.Work);
        Assert.AreEqual("9780000000028", isbn.Edition.Identifiers.Single().Value);

        var alternate = plan.Single(variant => variant.Kind == SearchVariantKind.AlternateTitle);
        Assert.AreEqual("Flamme de fer", alternate.Work.Title);
        Assert.AreEqual(baseVariant.Work.Authors[0], alternate.Work.Authors[0]);
        Assert.AreEqual(baseVariant.Edition, alternate.Edition);

        var series = plan.Single(variant => variant.Kind == SearchVariantKind.SeriesPosition);
        Assert.AreEqual("The Empyrean 2", series.Work.Title);
        Assert.AreEqual(baseVariant.Edition, series.Edition);

        Assert.IsTrue(plan.Where(variant => variant.Kind == SearchVariantKind.AuthorVariant)
            .All(variant => variant.Work.Title == baseVariant.Work.Title && variant.Work.Authors.Count == 1));
    }

    [TestMethod]
    public void ACollectorsEditionTitleThatReducesToTheBaseTitleIsNotAVariant()
    {
        var plan = SearchVariantPlanner.Plan(IronFlame("9780000000011"), RequestMediaType.Ebook);

        Assert.IsFalse(plan.Any(variant => variant.Key.Contains("COLLECTOR", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NoAuthorOnlyVariantIsEverPlanned()
    {
        // Protocol v2 requires work.title, so every variant keeps a title.
        var plan = SearchVariantPlanner.Plan(IronFlame("9780000000011", "9780000000028"), RequestMediaType.Ebook);

        Assert.IsTrue(plan.All(variant => variant.Work.Title.Length > 0));
    }

    [TestMethod]
    public void OtherIsbnsAreOrderedOrdinallyAndAreDeduplicated()
    {
        var plan = SearchVariantPlanner.Plan(
            IronFlame("9780000000011", "9780000000046", "9780000000028", "9780000000046"), RequestMediaType.Audiobook);

        AssertValues(plan.Where(variant => variant.Kind == SearchVariantKind.Isbn).Select(variant => variant.Key).ToArray(), "9780000000028", "9780000000046");
    }

    [TestMethod]
    public void PlanningIsDeterministic()
    {
        var first = SearchVariantPlanner.Plan(IronFlame("9780000000011", "9780000000028"), RequestMediaType.Ebook);
        var second = SearchVariantPlanner.Plan(IronFlame("9780000000011", "9780000000028"), RequestMediaType.Ebook);

        CollectionAssert.AreEqual(
            first.Select(variant => (variant.Kind, variant.Key)).ToArray(),
            second.Select(variant => (variant.Kind, variant.Key)).ToArray());
    }

    [TestMethod]
    public void AnIdentityWithNothingToVaryPlansOnlyTheBaseSearch()
    {
        var plan = SearchVariantPlanner.Plan(new BookIdentity("Moby Dick", "Melville", []), RequestMediaType.Ebook);

        Assert.HasCount(1, plan);
        Assert.AreEqual(SearchVariantKind.Base, plan[0].Kind);
    }

    [TestMethod]
    public void AQualifiedCatalogAuthorYieldsTheSameFormsAsThePlainName()
    {
        var plan = SearchVariantPlanner.Plan(
            new BookIdentity("The Terminal List", "Carr, Jack (Joint pseudonym)", []), RequestMediaType.Ebook);

        AssertValues(plan.Where(variant => variant.Kind == SearchVariantKind.AuthorVariant).Select(variant => variant.Key).ToArray(), "CARR, JACK", "J. CARR");
    }

    [TestMethod]
    public void AVeryLongKeyIsBounded()
    {
        var identity = new BookIdentity("Title", "A B", [], AlternateTitles: [new string('x', 1000)]);

        var key = SearchVariantPlanner.Plan(identity, RequestMediaType.Ebook)
            .Single(variant => variant.Kind == SearchVariantKind.AlternateTitle).Key;

        Assert.AreEqual(SearchVariantPlanner.MaxKeyLength, key.Length);
    }
}
