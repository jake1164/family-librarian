using FamilyLibrarian.Application.Security;

namespace FamilyLibrarian.Infrastructure.Tests.Security;

[TestClass]
public sealed class AudioPackagingMarkersTests
{
    [TestMethod]
    [DataRow("Onyx Storm (1 of 2) [Dramatized Adaptation]", 2, "Onyx Storm", true)]
    [DataRow("Onyx Storm (2 of 2)", 2, "Onyx Storm", false)]
    [DataRow("Onyx Storm [Part 2 of 2]", 2, "Onyx Storm", false)]
    [DataRow("Onyx Storm (Disc 1/3) (Full Cast Dramatization)", 3, "Onyx Storm", true)]
    [DataRow("Onyx Storm [Full-Cast]", null, "Onyx Storm", true)]
    [DataRow("Onyx Storm (Audio Drama)", null, "Onyx Storm", true)]
    // A part marker whose total disagrees with the set, or with no set at all, is left in place: that is a fragment.
    [DataRow("Onyx Storm (1 of 3)", 2, "Onyx Storm (1 of 3)", false)]
    [DataRow("Onyx Storm (1 of 2)", null, "Onyx Storm (1 of 2)", false)]
    // Only bracketed markers count: a real title containing the word is untouched.
    [DataRow("The Dramatized Life of Pets", null, "The Dramatized Life of Pets", false)]
    public void PackagingIsRemovedAndADramatizationIsReported(string title, int? setSize, string core, bool dramatized)
    {
        var packaging = AudioPackagingMarkers.Read(title, setSize);
        Assert.AreEqual(core, packaging.CoreTitle);
        Assert.AreEqual(dramatized, packaging.IsDramatized);
    }
}
