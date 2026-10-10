using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class AudiobookFormatPolicyTests
{
    [TestMethod]
    public void KeepsTheHighestPrioritySupportedAudiobookFormat()
    {
        var selected = AudiobookFormatPolicy.KeepHighestUsable(
        [
            Option("flac"),
            Option("ogg"),
            Option("opus"),
            Option("m4a"),
            Option("mp3"),
            Option("m4b")
        ]);

        Assert.HasCount(1, selected);
        Assert.AreEqual("m4b", selected[0].Format);
    }

    [TestMethod]
    public void KeepsATieAtTheBestSupportedFormatForReview()
    {
        var selected = AudiobookFormatPolicy.KeepHighestUsable([Option("mp3"), Option("MP3"), Option("m4a")]);

        Assert.HasCount(2, selected);
        Assert.IsTrue(selected.All(option => option.Format!.Equals("mp3", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void KeepsM4AAndAacAtTheSamePriority()
    {
        var selected = AudiobookFormatPolicy.KeepHighestUsable([Option("opus"), Option("m4a"), Option("aac")]);

        Assert.HasCount(2, selected);
        Assert.IsTrue(selected.Any(option => option.Format == "m4a"));
        Assert.IsTrue(selected.Any(option => option.Format == "aac"));
    }

    [TestMethod]
    public void TreatsOgaAsTheOggFallback()
    {
        var selected = AudiobookFormatPolicy.KeepHighestUsable([Option("flac"), Option("oga")]);

        Assert.HasCount(1, selected);
        Assert.AreEqual("oga", selected[0].Format);
    }

    [TestMethod]
    public void RejectsIgnoredAndUnknownAudiobookFormats()
    {
        var selected = AudiobookFormatPolicy.KeepHighestUsable(
            [Option("wav"), Option("aiff"), Option("wma"), Option("ape"), Option("amr")]);

        Assert.IsEmpty(selected);
    }

    [TestMethod]
    public void RecordsTheSelectedAudiobookFormatInTheControlledAuditSummary()
    {
        var summary = AudiobookFormatPolicy.DescribeAcquiredOption(Option("m4b"));

        StringAssert.Contains(summary, "Selected M4B under the automatic audiobook format policy.");
    }

    private static FulfillmentOption Option(string format) => new(
        "provider", Guid.NewGuid().ToString("N"), Guid.NewGuid(), null,
        RequestMediaType.Audiobook, OptionKind.DirectAcquisition, AcquisitionMethod.DirectDownload,
        format, "en", null, null, 0m, null, null, null, null, null);
}
