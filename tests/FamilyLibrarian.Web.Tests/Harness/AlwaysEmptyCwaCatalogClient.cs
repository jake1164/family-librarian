using FamilyLibrarian.Application.Matching;
using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe OPDS fake: "not found" is the ordinary outcome the pipeline already treats as expected.</summary>
internal sealed class AlwaysEmptyCwaCatalogClient : ICwaCatalogClient
{
    public Task<BookMatchResult> FindBookIdAsync(
        string title, string? author, IReadOnlyCollection<string> isbn13Candidates, CancellationToken cancellationToken) =>
        Task.FromResult(BookMatchResult.NoMatchResult);
}
