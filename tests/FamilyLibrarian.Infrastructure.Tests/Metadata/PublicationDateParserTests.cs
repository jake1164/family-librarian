using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Catalog;
using FamilyLibrarian.Infrastructure.Metadata;

namespace FamilyLibrarian.Infrastructure.Tests.Metadata;

[TestClass]
public sealed class PublicationDateParserTests
{
    private static readonly string[] Iso = ["yyyy-MM-dd"];
    private static readonly string[] Wordy = ["yyyy-MM-dd", "MMMM d, yyyy", "d MMM yyyy"];

    [TestMethod]
    [DataRow("2021-05-04", 2021, 5, 4, DatePrecision.Day)]
    [DataRow("2021-05", 2021, 5, 1, DatePrecision.Month)]
    [DataRow("May 2021", 2021, 5, 1, DatePrecision.Month)]
    [DataRow("1937", 1937, 1, 1, DatePrecision.Year)]
    [DataRow(" 1937 ", 1937, 1, 1, DatePrecision.Year)]
    public void APartialDateKeepsExactlyTheDetailTheSourceStated(string text, int year, int month, int day, DatePrecision precision)
    {
        var parsed = PublicationDateParser.Parse(text, Iso);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(new DateOnly(year, month, day), parsed.Value.Date);
        Assert.AreEqual(precision, parsed.Value.Precision);
        Assert.AreEqual(precision == DatePrecision.Day ? null : precision, PublicationDateParser.Stated(parsed),
            "A full date states no precision, so its historical meaning is unchanged.");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("sometime")]
    [DataRow("19370")]
    [DataRow("0000")]
    [DataRow("2021-13")]
    [DataRow("2021-02-30")]
    public void AnythingElseIsUnknownNeverGuessed(string? text) => Assert.IsNull(PublicationDateParser.Parse(text, Iso));

    [TestMethod]
    public void AProviderSpecificWordyFullDateIsADayButANumberAloneIsNotAMonthOrDay()
    {
        Assert.AreEqual(DatePrecision.Day, PublicationDateParser.Parse("September 21, 1937", Wordy)!.Value.Precision);
        Assert.AreEqual(DatePrecision.Day, PublicationDateParser.Parse("21 Sep 1937", Wordy)!.Value.Precision);
        Assert.AreEqual(DatePrecision.Year, PublicationDateParser.Parse("1937", Wordy)!.Value.Precision);
    }

    // ---- Hardcover's release_date: placeholder convention bounds the precision (H1) ----

    [TestMethod]
    [DataRow("1965-01-01", 1965, 1, 1, DatePrecision.Year)]
    [DataRow("0001-01-01", 1, 1, 1, DatePrecision.Year)]
    [DataRow("9999-01-01", 9999, 1, 1, DatePrecision.Year)]
    [DataRow("1965-06-01", 1965, 6, 1, DatePrecision.Month)]
    [DataRow("1965-12-01", 1965, 12, 1, DatePrecision.Month)]
    [DataRow("1965-06-02", 1965, 6, 2, DatePrecision.Day)]
    [DataRow("1969-11-30", 1969, 11, 30, DatePrecision.Day)]
    [DataRow("1964-12-31", 1964, 12, 31, DatePrecision.Day)]
    [DataRow("1965-01-02", 1965, 1, 2, DatePrecision.Day)]
    [DataRow("1965-02-01", 1965, 2, 1, DatePrecision.Month)]
    [DataRow("1965-06", 1965, 6, 1, DatePrecision.Month)]
    [DataRow("1965", 1965, 1, 1, DatePrecision.Year)]
    public void HardcoverReleaseDatesAreNeverMorePreciseThanTheDocumentedPlaceholderConventionAllows(
        string text, int year, int month, int day, DatePrecision precision)
    {
        var parsed = PublicationDateParser.ParseHardcoverRelease(text);

        Assert.IsNotNull(parsed);
        Assert.AreEqual(new DateOnly(year, month, day), parsed.Value.Date, "The stated value is kept exactly; only its precision is bounded.");
        Assert.AreEqual(precision, parsed.Value.Precision);
        Assert.AreEqual(precision == DatePrecision.Day ? null : precision, PublicationDateParser.Stated(parsed));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("sometime")]
    [DataRow("1965-13-01")]
    [DataRow("1965-02-30")]
    [DataRow("1965-06-01T00:00:00Z")]
    [DataRow("06/01/1965")]
    [DataRow("0000-01-01")]
    public void AnInvalidOrMissingHardcoverReleaseIsUnknownNeverGuessed(string? text) =>
        Assert.IsNull(PublicationDateParser.ParseHardcoverRelease(text));

    [TestMethod]
    public void OnlyHardcoverGetsTheConventionOtherProvidersFullDatesStayDays()
    {
        Assert.AreEqual(DatePrecision.Day, PublicationDateParser.Parse("1965-01-01", Iso)!.Value.Precision);
        Assert.AreEqual(DatePrecision.Day, PublicationDateParser.Parse("1965-06-01", Iso)!.Value.Precision);
    }

    [TestMethod]
    public void ACoarseHardcoverIntervalIsNeverCalledPublishedBeforeItHasEndedNorAnExactDay()
    {
        var thisYear = new DateOnly(2026, 6, 15);
        var yearStart = PublicationDateParser.ParseHardcoverRelease("2026-01-01")!.Value;
        var monthStart = PublicationDateParser.ParseHardcoverRelease("2026-06-01")!.Value;
        var nextYear = PublicationDateParser.ParseHardcoverRelease("2027-01-01")!.Value;
        var laterMonth = PublicationDateParser.ParseHardcoverRelease("2026-09-01")!.Value;
        var earlier = PublicationDateParser.ParseHardcoverRelease("2025-01-01")!.Value;
        var exact = PublicationDateParser.ParseHardcoverRelease("2026-06-14")!.Value;

        Assert.AreEqual(PublicationStatus.Unknown, Application.Catalog.Identity.PublicationStatusPolicy.Evaluate(yearStart.Date, yearStart.Precision, thisYear), "A year that contains today has not ended: nothing is claimed.");
        Assert.AreEqual(PublicationStatus.Unknown, Application.Catalog.Identity.PublicationStatusPolicy.Evaluate(monthStart.Date, monthStart.Precision, thisYear), "The current month has not ended.");
        Assert.AreEqual(PublicationStatus.Forthcoming, Application.Catalog.Identity.PublicationStatusPolicy.Evaluate(nextYear.Date, nextYear.Precision, thisYear));
        Assert.AreEqual(PublicationStatus.Forthcoming, Application.Catalog.Identity.PublicationStatusPolicy.Evaluate(laterMonth.Date, laterMonth.Precision, thisYear));
        Assert.AreEqual(PublicationStatus.Published, Application.Catalog.Identity.PublicationStatusPolicy.Evaluate(earlier.Date, earlier.Precision, thisYear));
        Assert.AreEqual(PublicationStatus.Published, Application.Catalog.Identity.PublicationStatusPolicy.Evaluate(exact.Date, exact.Precision, thisYear));
    }

    [TestMethod]
    public void TheLanguageNormalizationUsedByProvidersAndPolicyIsOneFunction()
    {
        Assert.AreEqual("en", LanguageCodeNormalizer.Normalize("eng"));
        Assert.AreEqual("en", LanguageCodeNormalizer.Normalize("en-US"));
        Assert.AreEqual("de", CatalogLanguage.Normalize("GER"), "Open Library reports the MARC bibliographic code.");
        Assert.AreEqual("de", CatalogLanguage.Normalize("deu"));
        Assert.AreEqual("fr", CatalogLanguage.Normalize("fre"));
        Assert.IsNull(LanguageCodeNormalizer.Normalize("english"));
        Assert.IsNull(LanguageCodeNormalizer.Normalize(null));
    }
}
