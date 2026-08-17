using FamilyLibrarian.Application.Requests;

namespace FamilyLibrarian.Domain.Tests.Architecture;

/// <summary>
/// The Application layer's dependency boundary, asserted the same way
/// <see cref="DomainBoundaryTests"/> asserts the Domain layer's.
/// </summary>
/// <remarks>
/// <c>AGENTS.md</c> requires that "domain and application logic must not depend
/// on Blazor, EF Core, or vendor SDKs." Until this test existed that held for
/// Application only because nobody had added the package — and the layer had in
/// fact been carrying a <c>FamilyLibrarian.Contracts</c> reference long after
/// the last real use of it went away.
/// <para>
/// This catches a forbidden dependency that is actually <em>used</em>, which is
/// the architectural invariant that matters. It cannot catch a dead
/// <c>ProjectReference</c> on its own: the compiler omits an assembly reference
/// that no code consumes, so an unused one leaves no trace in the metadata this
/// inspects. The unused-using build error added in <c>.editorconfig</c> catches
/// the usual symptom; the <c>.csproj</c> itself still needs a human eye.
/// </para>
/// </remarks>
[TestClass]
public sealed class ApplicationBoundaryTests
{
    /// <summary>
    /// Matched by prefix, not equality. Real assembly names are
    /// <c>Microsoft.EntityFrameworkCore.Relational</c> and
    /// <c>Microsoft.AspNetCore.Http.Abstractions</c>, not the bare roots — so an
    /// equality check would let every sub-assembly through while appearing to
    /// forbid the whole family.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "FamilyLibrarian.Contracts",
        "FamilyLibrarian.Infrastructure",
        "FamilyLibrarian.Web",
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "MudBlazor",
        "Npgsql",
        "Renci.SshNet"
    ];

    [TestMethod]
    public void ApplicationAssemblyDependsOnDomainOnly()
    {
        var references = typeof(BookRequestService).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .ToArray();

        // Proves the reflection above actually resolved something, so a future
        // change that makes the reference list empty fails loudly instead of
        // making every assertion below vacuously true.
        CollectionAssert.Contains(
            references,
            "FamilyLibrarian.Domain",
            "The Application assembly should reference Domain; the reference list looks wrong.");

        var violations = references
            .Where(reference => ForbiddenPrefixes.Any(
                prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(
            0,
            violations.Length,
            $"Application must not depend on presentation, persistence, or vendor SDKs. Found: {string.Join(", ", violations)}");
    }
}
