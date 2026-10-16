using FamilyLibrarian.Domain.Requests;

namespace FamilyLibrarian.Domain.Tests.Requests;

[TestClass]
public sealed class RequesterAcquisitionPreferenceTests
{
    [TestMethod]
    [DataRow("AcceptCollectionsContaining", null, "")]
    [DataRow("AcceptLanguage", "FR", "fr")]
    public void PreferenceValuesAreNormalizedForTheUniqueKey(string kind, string? value, string normalized)
    {
        var type = typeof(BookRequest).Assembly.GetType("FamilyLibrarian.Domain.Requests.RequesterAcquisitionPreference");
        Assert.IsNotNull(type);
        var kindType = typeof(BookRequest).Assembly.GetType("FamilyLibrarian.Domain.Requests.RequesterAcquisitionPreferenceKind");
        Assert.IsNotNull(kindType);
        var preference = Activator.CreateInstance(type, Guid.NewGuid(), Enum.Parse(kindType, kind), value, DateTimeOffset.UtcNow);
        Assert.AreEqual(normalized, type.GetProperty("Value")!.GetValue(preference));
    }
}
