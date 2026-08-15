using FamilyLibrarian.Application.Publishing;

namespace FamilyLibrarian.Web.Tests.Harness;

/// <summary>Default-safe OPDS fake: "not found" is the ordinary outcome the pipeline already treats as expected.</summary>
internal sealed class AlwaysEmptyCwaCatalogClient : ICwaCatalogClient
{
    public Task<string?> FindBookIdAsync(string title, string? author, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
