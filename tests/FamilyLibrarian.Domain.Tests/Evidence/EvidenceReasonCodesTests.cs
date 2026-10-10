using System.Reflection;
using System.Text.RegularExpressions;
using FamilyLibrarian.Domain.Evidence;

namespace FamilyLibrarian.Domain.Tests.Evidence;

[TestClass]
public sealed partial class EvidenceReasonCodesTests
{
    [GeneratedRegex(@"^[a-z][a-z0-9]*(_[a-z0-9]+)*(\.[a-z][a-z0-9]*(_[a-z0-9]+)*){2}$", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormed();

    private static (string Field, string Code)[] AllCodes() =>
        typeof(EvidenceReasonCodes).GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(group => group.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                .Select(field => ($"{group.Name}.{field.Name}", (string)field.GetRawConstantValue()!)))
            .ToArray();

    [TestMethod]
    public void TheCatalogIsNotEmpty() => Assert.IsGreaterThan(30, AllCodes().Length);

    [TestMethod]
    public void EveryCodeIsLowerSnakeCaseWithThreeDottedSegments()
    {
        var malformed = AllCodes().Where(item => !WellFormed().IsMatch(item.Code)).Select(item => $"{item.Field}={item.Code}").ToArray();
        Assert.IsEmpty(malformed, string.Join(", ", malformed));
    }

    [TestMethod]
    public void CodesAreUniqueSoOneCodeNeverMeansTwoThings()
    {
        var duplicates = AllCodes().GroupBy(item => item.Code).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        Assert.IsEmpty(duplicates, string.Join(", ", duplicates));
    }

    [TestMethod]
    public void CodesNameObservationsNotDecisions()
    {
        var decisionWords = new[] { "accept", "approve", "reject", "trusted", "publish" };
        var offenders = AllCodes().Where(item => decisionWords.Any(word => item.Code.Contains(word, StringComparison.Ordinal)))
            .Select(item => item.Code).ToArray();
        Assert.IsEmpty(offenders, string.Join(", ", offenders));
    }

    [TestMethod]
    public void FindingIdsAndPolaritiesAreStableValues()
    {
        // Persisted/serialized by name or number later; pin the numbers.
        Assert.AreEqual(1, (int)EvidencePolarity.Positive);
        Assert.AreEqual(2, (int)EvidencePolarity.Weak);
        Assert.AreEqual(3, (int)EvidencePolarity.Negative);
        Assert.AreEqual(4, (int)EvidencePolarity.Unknown);
        Assert.AreEqual(5, (int)EvidencePolarity.Condition);
    }
}
