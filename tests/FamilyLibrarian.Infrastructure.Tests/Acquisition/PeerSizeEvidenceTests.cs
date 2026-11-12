using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Application.Catalog;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class PeerSizeEvidenceTests
{
    [TestMethod]
    public void MedianExcludesChosenAndUnknownSizesAndNormalizesTheSameFormat()
    {
        var chosen = Option("chosen", 600_000);
        var peers = new[] { chosen, Option("large", 6_000_000), Option("small", 2_000_000), Option("middle", 4_000_000, ".EPUB"),
            Option("unknown", null), Option("zero", 0), Option("negative", -1), Option("other-format", 20_000_000, "pdf"),
            Option("other-source", 20_000_000) with { ProviderId = "another-source" },
            Option("other-media", 20_000_000) with { MediaType = RequestMediaType.Audiobook } };
        var evidence = PeerSizeEvidence.From(chosen, peers);
        Assert.IsNotNull(evidence);
        Assert.AreEqual(4_000_000L, evidence.MedianSizeBytes);
        Assert.AreEqual(3, evidence.Count);
        Assert.IsNull(PeerSizeEvidence.From(chosen, peers.Take(3).ToArray()));
        Assert.IsNull(PeerSizeEvidence.From(chosen with { Format = null }, peers));
    }

    [TestMethod]
    public void EvenMedianCannotOverflowAtLongSizes()
    {
        var chosen = Option("chosen", 1);
        var evidence = PeerSizeEvidence.From(chosen, [Option("1", long.MaxValue - 6), Option("2", long.MaxValue - 4),
            Option("3", long.MaxValue - 2), Option("4", long.MaxValue)]);
        Assert.IsNotNull(evidence);
        Assert.AreEqual(long.MaxValue - 3, evidence.MedianSizeBytes);
        Assert.AreEqual(4, evidence.Count);
    }

    private static FulfillmentOption Option(string id, long? size, string format = "epub") =>
        new("example-source", id, Guid.Empty, null, RequestMediaType.Ebook, OptionKind.DirectAcquisition,
            AcquisitionMethod.DirectDownload, format, "en", null, null, null, null, null, null, null, null,
            SizeBytes: size);
}
