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
