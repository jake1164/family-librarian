using FamilyLibrarian.Application.Acquisition;
using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Infrastructure.Tests.Acquisition;

[TestClass]
public sealed class ExternalProviderOutputPolicyTests
{
    [TestMethod]
    public void AudiobooksAllowSingleFilesAboveOneGibibyteButEbooksDoNot()
    {
        var policy = new ExternalProviderOutputPolicy();
        const long oneAndAHalfGiB = 3L * 512 * 1024 * 1024;

        Assert.IsGreaterThan(oneAndAHalfGiB, policy.ForMediaType(RequestMediaType.Audiobook).MaxFileBytes);
        Assert.IsLessThan(oneAndAHalfGiB, policy.ForMediaType(RequestMediaType.Ebook).MaxFileBytes);
    }

    [TestMethod]
    public void NoAcceptedFileIsLargerThanClamdCanScan()
    {
        // compose.yaml sets clamd's StreamMaxLength/MaxFileSize to 2000M, and
        // ClamAV cannot scan files over ~2 GB at all. A file the policy admits
        // but clamd cannot scan would sit in Quarantine forever.
        const long clamdCeilingBytes = 2000L * 1024 * 1024;
        var policy = new ExternalProviderOutputPolicy();

        Assert.IsLessThanOrEqualTo(clamdCeilingBytes, policy.ForMediaType(RequestMediaType.Audiobook).MaxFileBytes);
        Assert.IsLessThanOrEqualTo(clamdCeilingBytes, policy.ForMediaType(RequestMediaType.Ebook).MaxFileBytes);
    }

    [TestMethod]
    public void ResolvedPolicyKeepsTheSharedSettings()
    {
        var policy = new ExternalProviderOutputPolicy { MinFreeDiskBytes = 42, MaxFilenameLength = 7 };

        var resolved = policy.ForMediaType(RequestMediaType.Audiobook);

        Assert.AreEqual(42, resolved.MinFreeDiskBytes);
        Assert.AreEqual(7, resolved.MaxFilenameLength);
    }

    [TestMethod]
    public void FallsBackToFlatLimitsWhenAMediaTypeHasNoneOfItsOwn()
    {
        var policy = new ExternalProviderOutputPolicy { Ebook = null, MaxFileBytes = 5, MaxJobBytes = 9 };

        Assert.AreEqual(5, policy.ForMediaType(RequestMediaType.Ebook).MaxFileBytes);
    }

    [TestMethod]
    public void RejectsPerMediaTypeJobLimitBelowFileLimit()
    {
        var policy = new ExternalProviderOutputPolicy
        {
            Audiobook = new ExternalProviderOutputLimits { MaxOutputCount = 1, MaxFileBytes = 10, MaxJobBytes = 5 }
        };

        Assert.IsFalse(policy.IsValid);
    }

    [TestMethod]
    public void RejectsNegativeMinimumFreeDisk()
    {
        Assert.IsFalse(new ExternalProviderOutputPolicy { MinFreeDiskBytes = -1 }.IsValid);
    }
}
